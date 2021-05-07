﻿using CoAPNet.Options;

using Microsoft.Extensions.DependencyInjection;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace CoAPNet.Middleware
{
	// Mimicking https://docs.microsoft.com/en-us/aspnet/core/fundamentals/middleware/write?view=aspnetcore-5.0
	// NOTE: Not going whole RequestDelegate route just to save up front time while we prove the concept
	public interface ICoapMiddleware //: ICoapHandler
	{
		Task Invoke(CoapContext context);
	}

	public class CoapContext
	{
		public ICoapConnectionInformation Connection { get; }
		/// <summary>
		/// Incoming message
		/// </summary>
		public CoapMessage Message { get; }
		public DateTimeOffset DateTimeReceived { get; }

		public CancellationToken CancellationToken { get; }

		public System.Collections.ObjectModel.ObservableCollection<
			Tuple<ICoapEndpoint, CoapMessage>> Outgoing
		{ get; }

		public IServiceProvider Services { get; }

		public CoapContext(ICoapConnectionInformation connection, byte[] payload,
			DateTimeOffset received,
			IServiceProvider services,
			CancellationToken cancellationToken)
		{
			Connection = connection;
			Message = new CoapMessage();
			Message.OptionFactory = services.GetService<OptionFactory>();
			Message.Decode(new System.IO.MemoryStream(payload));
			DateTimeReceived = received;
			CancellationToken = cancellationToken;
			Services = services;
			Outgoing = new System.Collections.ObjectModel.ObservableCollection<Tuple<ICoapEndpoint, CoapMessage>>();
		}
	}


	public static class CoapContextExtensions
	{
		public static CoapMessage Reply(this CoapContext context,
			CoapMessageCode code,
			CoapMessageType type = CoapMessageType.Acknowledgement)
		{
			var m = new CoapMessage
			{
				Id = context.Message.Id,
				Token = context.Message.Token,
				Type = type,
				Code = code
			};
			context.Outgoing.Add(new Tuple<ICoapEndpoint, CoapMessage>(context.Connection.RemoteEndpoint, m));
			return m;
		}
	}


	/// <summary>
	/// Looks for ACKs after a CON is sent out
	/// </summary>
	public class CoapAckMiddleware : ICoapMiddleware
	{
		/// <summary>
		/// Looks for an ack from a single MID/endpoint combo
		/// </summary>
		public class SingleTarget : ICoapMiddleware
		{
			readonly DateTimeOffset requestSent;
			// remote endpoint
			readonly ICoapEndpoint endpoint;
			DateTimeOffset responseReceived = DateTimeOffset.MinValue;
			// TODO: Get proper linger value for us to continue consuming ACKs in case of duplicates
			readonly TimeSpan linger = TimeSpan.FromSeconds(20);
			// The message that was sent with CON.  Need to hold on to this in case of retry (resend)
			protected readonly CoapMessage sent;

			/// <summary>
			/// Remote endpoint to attempt a retry to if need be
			/// </summary>
			public ICoapEndpoint Endpoint => endpoint;
			public DateTimeOffset DateTimeSent => requestSent;
			public UInt16 Id => (UInt16)sent.Id;
			public DateTimeOffset DateTimeReceived => responseReceived;

			public SingleTarget(CoapMessage sent, DateTimeOffset requestSent, ICoapEndpoint endpoint)
			{
				this.sent = sent;
				this.requestSent = requestSent;
				this.endpoint = endpoint;
			}


			/// <summary>
			/// Fired at most once to reflect an ACK was received
			/// </summary>
			/// <remarks>
			/// NOTE: Name might be better as "Acknowledged", "AckReceived" etc.  
			/// Naming is a little tricky because we dedup potentially multiple ACKs
			/// </remarks>
			public event Action<SingleTarget> AckEncountered;

			public Task Invoke(CoapContext context)
			{
				if (context.Message.Id == Id && context.Message.Type == CoapMessageType.Acknowledgement)
				{
					if (responseReceived == DateTimeOffset.MinValue)
					{
						AckEncountered(this);
						responseReceived = context.DateTimeReceived;
					}
				}

				return Task.CompletedTask;
			}


			/// <summary>
			/// Returns true when maximum retries is reached and no ACK seen
			/// </summary>
			public bool Expired
			{
				get
				{
					return false;
				}
			}


			/// <summary>
			/// Indicates to owner task that no further processing is required from this target
			/// </summary>
			public bool ShouldScavenge
			{
				get
				{
					return Expired || ((responseReceived + linger) > DateTimeOffset.Now);
				}
			}
		}

		// DEBT: A lot of optimization opportunity here
		protected List<SingleTarget> targets = new List<SingleTarget>();

		readonly RequestDelegate<CoapContext> next;

		/// <summary>
		/// Rebroadcasts AckEncountered event originating from SingleTarget
		/// Refer to <see cref="SingleTarget.AckEncountered"/>
		/// </summary>
		public event Action<SingleTarget> AckEncountered;

		public CoapAckMiddleware(RequestDelegate<CoapContext> next)
		{
			this.next = next;
		}

		public Task Invoke(CoapContext context)
		{
			LinkedList<SingleTarget> toScavenge = new LinkedList<SingleTarget>();

			foreach (SingleTarget target in targets)
			{
				// NOTE: Beware, we may have to get clever with looking at different ports here
				if (target.Endpoint == context.Connection.RemoteEndpoint)
				{
					target.Invoke(context);
				}

				if (target.ShouldScavenge) toScavenge.AddLast(target);
			}

			foreach (SingleTarget target in toScavenge)
			{
				target.AckEncountered -= Target_AckEncountered;
				targets.Remove(target);
			}


			return next.Invoke(context);
		}

		protected void Add(SingleTarget target)
		{
			targets.Add(target);

			target.AckEncountered += Target_AckEncountered;
		}



		public void Track(CoapMessage message, ICoapEndpoint endpoint, DateTimeOffset dateTimeSent)
		{
			var target = new SingleTarget(message, dateTimeSent, endpoint);
			Add(target);
		}

		private void Target_AckEncountered(SingleTarget target)
		{
			AckEncountered?.Invoke(target);
		}
	}


	public class CoapRetryMiddleware : CoapAckMiddleware
	{
		readonly Experimental.SchedulerService scheduler;

		/// <summary>
		/// 
		/// </summary>
		/// <remarks>
		/// https://tools.ietf.org/html/rfc7252#section-4.8
		/// </remarks>
		public class Parameters
		{
			public TimeSpan AckTimeout { get; set; } = TimeSpan.FromSeconds(2);
			public float AckRandomFactor { get; set; } = 1.5F;
			public ushort MaxRetransmit { get; set; } = 4;
		}

		class RetryTarget : SingleTarget
		{
			// DEBT: Make these configurable
			// https://tools.ietf.org/html/rfc7252#section-4.8
			// all time values in ms
			const uint ackTimeout = 2000;
			const uint defaultLeisure = 5000;

			// https://tools.ietf.org/html/rfc7252#section-4.8.2
			const uint maxTransmitSpan = (uint)(ackTimeout * ((maxRetransmit * maxRetransmit) - 1) * ackRandomFactor);

			const uint maxRetransmit = 4;
			// DEBT: Make this actually random
			const float ackRandomFactor = 1.5F;

			readonly Experimental.SchedulerService scheduler;
			readonly Parameters parameters = new Parameters();

			ushort retries = 0;

			public RetryTarget(CoapMessage sent, DateTimeOffset requestSent, ICoapEndpoint endpoint,
				Experimental.SchedulerService scheduler) : base(sent, requestSent, endpoint)
			{
				this.scheduler = scheduler;

			}

			void Retry(DateTimeOffset now, ICoapEndpoint localEndpoint, CancellationToken ct)
			{
				if (DateTimeReceived == DateTimeOffset.MinValue)
				{
					CoapPacket p = sent.ToPacket(Endpoint);
					retries++;
					// DEBT: Heed NSTART
					localEndpoint.SendAsync(p, ct);
					if (retries <= maxRetransmit)
						ScheduleRetry(localEndpoint, ct);
				}
			}

			public void ScheduleRetry(ICoapEndpoint localEndpoint, CancellationToken ct)
			{
				// https://tools.ietf.org/html/rfc7252#section-4.2
				var doubler = Math.Pow(2, retries);
				//var timeout = ackTimeout * ackRandomFactor * doubler;
				var timeout = parameters.AckTimeout.TotalMilliseconds *
					parameters.AckRandomFactor * doubler;
				var toRetry = DateTimeSent.AddMilliseconds(timeout);

				var scheduledItem = scheduler.AddOneShot(toRetry.DateTime,
					() => Retry(toRetry, localEndpoint, ct),
					$"retry handler:mid={Id}");
			}
		}

		public CoapRetryMiddleware(RequestDelegate<CoapContext> next,
			Experimental.SchedulerService scheduler) : base(next)
		{
			this.scheduler = scheduler;
		}

		public void Track(CoapMessage message, ICoapEndpoint endpoint, DateTimeOffset dateTimeSent,
			ICoapEndpoint localEndpoint, CancellationToken retrySendCancellationToken)
		{
			var target = new RetryTarget(message, dateTimeSent, endpoint, scheduler);
			target.ScheduleRetry(localEndpoint, retrySendCancellationToken);
			Add(target);
		}
	}

	/// <summary>
	/// Server-side portion of CoAP observe option
	/// </summary>
	public class CoapObserveMiddleware : ICoapMiddleware
	{
		public class Observer
		{
			public ICoapEndpoint Endpoint { get; set; }
		}

		public class Subject
		{
			/// <summary>
			/// Local endpoint which subject listened/originates from
			/// </summary>
			public ICoapEndpoint Endpoint { get; set; }

			public Uri Uri { get; set; }

			public uint Counter { get; set; }

			public List<Observer> Observers { get; set; } = new List<Observer>();

			public async Task SendAsync(CoapMessage message, CancellationToken ct)
			{
				var ms = new System.IO.MemoryStream();
				message.Encode(ms);

				foreach (Observer observer in Observers)
				{
					var packet = new CoapPacket
					{
						Endpoint = observer.Endpoint,
						Payload = ms.GetBuffer()
					};
					// DEBT: Do a Task.WaitAll here
					// TODO: Rely on CoapAckMiddleware to resolve CON outgoing packets here
					await Endpoint.SendAsync(packet, ct);
				}
			}
		}

		readonly RequestDelegate<CoapContext> next;

		List<Subject> subjects = new List<Subject>();

		public CoapObserveMiddleware(RequestDelegate<CoapContext> next)
		{
			this.next = next;
		}

		public event Action<Subject, Observer> Registered;
		public event Action<Subject, Observer> Deregistered;

		public Task Invoke(CoapContext context)
		{
			var o = context.Message.Options.OfType<Options.Observe>().SingleOrDefault();

			if (o != null)
			{
				Uri uri = context.Message.GetUri();
				Subject subject = subjects.SingleOrDefault(x => x.Uri == uri);

				if (subject == null)
				{
					subject = new Subject
					{
						Uri = uri,
						Endpoint = context.Connection.LocalEndpoint
					};
					subjects.Add(subject);
				}

				// client -> server observe:0 means register
				if (o.Value == 0)
				{
					var observer = new Observer
					{
						Endpoint = context.Connection.RemoteEndpoint
					};
					subject.Observers.Add(observer);
					Registered?.Invoke(subject, observer);
				}
				// client -> server observe:1 means deregister
				else
				{
					// DEBT: Validate and make sure this is a 1
					// FIX: Put in subject.Observers.Remove code
					//Deregistered?.Invoke(subject, observer);
				}
			}

			return next.Invoke(context);
		}
	}

	public delegate Task RequestDelegate<TContext>(TContext context);

	public interface IApplicationBuilder<TContext>
	{
		/// <summary>
		/// Builds the delegate for using the complete middleware pipeline
		/// </summary>
		/// <returns></returns>
		RequestDelegate<TContext> Build();

		/// <summary>
		/// Adds a middleware delegate to the request pipeline
		/// </summary>
		/// <param name="middleware"></param>
		/// <returns></returns>
		IApplicationBuilder<TContext> Use(
			Func<RequestDelegate<TContext>, RequestDelegate<TContext>> middleware);

		IApplicationBuilder<TContext> New();
	}


	public static class ApplicationBuilderExtensions
	{
		public static IApplicationBuilder<TContext> Use<TContext>(
			this IApplicationBuilder<TContext> appBuilder, Func<TContext, Func<Task>, Task> middleware)
		{
			// Set up middleware pseudo-factory...
			appBuilder.Use((RequestDelegate<TContext> next) =>
			{
				// Create native-format middleware activation delegate
				RequestDelegate<TContext> d = async (TContext context) =>
				{
					// call friendlier inline middleware, including adapted
					// form of next awaiter
					await middleware(context, () => next(context));
				};

				return d;
			});

			return appBuilder;
		}

		/*
		public static RequestDelegate<TContext> Build<TContext>(
			this IApplicationBuilder<TContext> appBuilder, RequestDelegate<TContext> middleware)
		{
			// Set up middleware pseudo-factory...
			appBuilder.Use((RequestDelegate<TContext> next) => middleware);
			return appBuilder.Build();
		} */
	}

	public class ApplicationBuilder<TContext> : IApplicationBuilder<TContext>
	{
		LinkedList<Func<RequestDelegate<TContext>, RequestDelegate<TContext>>> middlewares =
			new LinkedList<Func<RequestDelegate<TContext>, RequestDelegate<TContext>>>();

		public RequestDelegate<TContext> Build()
		{
			RequestDelegate<TContext> d = _ => Task.CompletedTask;
			foreach (var m in middlewares)
			{
				// activate middleware pseudo-factory which takes previous
				// resulting delegate (aka 'next') as input
				d = m(d);
			}

			return d;
		}

		public IApplicationBuilder<TContext> New()
		{
			throw new NotImplementedException();
		}

		public IApplicationBuilder<TContext> Use(Func<RequestDelegate<TContext>, RequestDelegate<TContext>> middleware)
		{
			middlewares.AddFirst(middleware);
			return this;
		}
	}





	public class CoapTerminatorMiddleware : ICoapMiddleware
	{
		public Task Invoke(CoapContext _) => Task.CompletedTask;
	}


	// EXPERIMENTAL
	// DEBT: Acts a a client-and-server actually, so wants a different name
	public class CoapClient2
	{
		public ICoapEndpoint Endpoint { get; }

		ICoapMiddleware middleware;

		readonly OptionFactory optionFactory;
		readonly ApplicationBuilder<CoapContext> appBuilder = new ApplicationBuilder<CoapContext>();

		public CoapClient2(ICoapEndpoint endpoint, IServiceProvider services,
			CancellationToken ct,
			// FIX: Need to grab middlewares from IServiceProvdier, which it's looking like we'll
			// need to do ASP.NET Core RequestDelegate style to truly pull off due to trickiness
			// of initializing that constructor with the next middleware.  As it stands, the
			// provided 'extra' will need CoapTerminatorMiddleware (or similar) as its 'next'
			ICoapMiddleware extra = null)
		{
			Endpoint = endpoint;

			// DEBT: Processed in reverse order from what you see here.  Would definitely be better
			// to register in more natural forward order and an overall easier to understand way
			ICoapMiddleware terminator = new CoapTerminatorMiddleware();

			if (extra != null)
				terminator = extra;

			var scheduler = services.GetService<Experimental.SchedulerService>();
			var observeMiddleware = new CoapObserveMiddleware(terminator.Invoke);
			var ackMiddleware = new CoapAckMiddleware(observeMiddleware.Invoke);

			//var ackMiddleware = ActivatorUtilities.CreateInstance<CoapAckMiddleware>(services, 
			//new RequestDelegate<CoapContext>(observeMiddleware.Invoke));

			middleware = ackMiddleware;
			optionFactory = new OptionFactory(new[] { typeof(Options.Observe) });

			appBuilder.Use(requestDelegate =>
				new CoapRetryMiddleware(requestDelegate, scheduler).Invoke);
			appBuilder.Use(requestDelegate =>
				new CoapObserveMiddleware(requestDelegate).Invoke);

			Task.Run(() => Worker(services, ct));
		}


		// NOTE: Consider making a Fact service -- not doing so because that might be more heavy
		// than is needed it seems
		async Task Worker(IServiceProvider services, CancellationToken ct)
		{
			while (!ct.IsCancellationRequested)
			{
				CoapPacket packet = await Endpoint.ReceiveAsync(ct);
				var now = DateTimeOffset.Now;
				var c = new CoapConnectionInformation(Endpoint, packet.Endpoint);

				var context = new CoapContext(c, packet.Payload, now, services, ct);

				await middleware.Invoke(context);

				foreach (var outgoing in context.Outgoing)
				{
					var p = new CoapPacket
					{
						Endpoint = outgoing.Item1,
						Payload = outgoing.Item2.ToBytes()
					};

					// DEBT: Paralellize this portion
					await Endpoint.SendAsync(p, ct);
				}
			}
		}
	}


	public class CoapConnectionInformation : ICoapConnectionInformation
	{
		public ICoapEndpoint LocalEndpoint { get; }

		public ICoapEndpoint RemoteEndpoint { get; }

		public CoapConnectionInformation(ICoapEndpoint local, ICoapEndpoint remote)
		{
			LocalEndpoint = local;
			RemoteEndpoint = remote;
		}
	}


	public static class CoapMessageExtensions
	{
		public static CoapPacket ToPacket(this CoapMessage message, ICoapEndpoint endpoint)
		{
			using (var ms = new System.IO.MemoryStream())
			{
				message.Encode(ms);
				return new CoapPacket
				{
					Payload = ms.ToArray(),
					Endpoint = endpoint
				};
			}
		}
	}
}