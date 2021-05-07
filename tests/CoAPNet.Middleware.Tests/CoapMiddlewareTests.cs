using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace CoAPNet.Middleware.Tests
{
    public class CoapMiddlewareTests
    {
        IServiceProvider services;

        void AddOneShot(DateTime now, DateTime later, Action action, string desc)
        {
        }

        [SetUp]
        public void Setup()
        {
            var sc = new ServiceCollection();

            sc.AddSingleton(x =>
            {
                return new CoAPNet.Options.OptionFactory(new[] { typeof(Options.Observe) });
            });

            services = sc.BuildServiceProvider();
        }

        CoapMessage CreateGetMessage()
        {
            var message = new CoapMessage();
            message.Code = CoapMessageCode.Get;
            message.Type = CoapMessageType.Confirmable;
            message.Id = 1;
            return message;
        }

        CoapMessage CreateAckMessage(int mid)
        {
            var ackMessage = new CoapMessage();
            ackMessage.Code = CoapMessageCode.Valid;
            ackMessage.Type = CoapMessageType.Acknowledgement;
            ackMessage.Id = mid;
            return ackMessage;
        }

        Task Termination(CoapContext _) => Task.CompletedTask;

        readonly DateTime dtNow = new DateTime(1999, 1, 1, 12, 0, 0);


        [Test]
        public void TestAck1()
        {
            CoapMessage message = CreateGetMessage();
            CoapMessage ackMessage = CreateAckMessage(message.Id);
            var conn = new CoapConnectionInformation(
                new CoapEndpoint(), 
                new CoapEndpoint());

            var packet = ackMessage.ToPacket(conn.RemoteEndpoint);
            var dtSent = dtNow;
            var dtReceived = new DateTimeOffset(dtNow).AddSeconds(4);
            var cts = new CancellationTokenSource();
            var c = new CoapContext(conn, packet.Payload, dtReceived, services, cts.Token);
            var m = new CoapAckMiddleware(Termination);

            m.Track(message, conn.RemoteEndpoint, dtSent);

            m.Invoke(c);

        }

        [Test]
        public void TestRetry1()
        {
            RequestDelegate<CoapContext> d = (CoapContext c) => Task.CompletedTask;

            CoapMessage message = CreateGetMessage();
            CoapMessage ackMessage = CreateAckMessage(message.Id);

            var localEndpoint = new CoapEndpoint();
            var remoteEndpoint = new CoapEndpoint();
            var packet = ackMessage.ToPacket(remoteEndpoint);
            var dtSent = dtNow;
            var dtReceived = new DateTimeOffset(dtNow).AddSeconds(4);
            var conn = new CoapConnectionInformation(null, remoteEndpoint);
            var cts = new CancellationTokenSource();
            var c = new CoapContext(conn, packet.Payload, dtReceived, services, cts.Token);
            var m = new CoapRetryMiddleware(d, (absolute, action, desc) =>
                AddOneShot(dtNow, absolute, action, desc));

            m.Track(message, remoteEndpoint, dtSent, localEndpoint, cts.Token);

            m.Invoke(c);

            //Assert.Pass();
        }
    }
}