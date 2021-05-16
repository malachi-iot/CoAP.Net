using FluentAssertions;
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

            sc.AddTransient(sp => new AddOneShotDelegate((scheduled, action, x) => {}));
            sc.AddSingleton(x =>
            {
                return new CoAPNet.Options.OptionFactory(new[] { typeof(Options.Observe) });
            });

            services = sc.BuildServiceProvider();
        }

        CoapMessage CreateGetMessage(CoapMessageType type = CoapMessageType.Confirmable)
        {
            var message = new CoapMessage();
            message.Code = CoapMessageCode.Get;
            message.Type = type;
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


        [Test]
        public async Task ClientTest1()
        {
            var cts = new CancellationTokenSource();
            var local = new SyntheticCoapEndpoint("coap://localhost");
            var remote = new SyntheticCoapEndpoint("coap://remotehost");
            var middleware = new SyntheticMiddleware();
            var c = new CoapClient2(local, services, cts.Token, extra: middleware);

            CoapMessage m = CreateGetMessage();

            m.SetUri("coap://localhost/v1/hello");
            m.Id = new Random().Next(0, UInt16.MaxValue);

            CoapPacket cp = m.ToPacket(remote);

            //cts.CancelAfter(TimeSpan.FromSeconds(5));

            // simulate receipt of a CON message
            await local.EnqueueFromTransport(cp, cts.Token);

            // forcefully retrieve whatever we would have emitted over transport in response
            var _out = await local.DequeueToTransport(cts.Token);
            var response = CoapMessage.CreateFromBytes(_out.Payload);

            // ACK generated from middleware
            response.Id.Should().Be(m.Id);
            response.Type.Should().Be(CoapMessageType.Acknowledgement);

            cts.Cancel();
        }


        [Test]
        public async Task ClientTest2()
        {
            var cts = new CancellationTokenSource();
            var local = new SyntheticCoapEndpoint("coap://localhost");
            var remote = new SyntheticCoapEndpoint("coap://remotehost");
            var middleware = new SyntheticMiddleware();
            var c = new CoapClient2(local, services, cts.Token, extra: middleware);
            CoapMessage m = CreateGetMessage();

            m.SetUri("coap://localhost/v1/hello");
            m.Id = new Random().Next(0, UInt16.MaxValue);

            CoapPacket cp = m.ToPacket(remote);

            //cts.CancelAfter(TimeSpan.FromSeconds(5));

            // Send directly via client out over simulated transport
            await c.SendAsync(m, remote, cts.Token);

            //await local.SendAsync(cp, cts.Token);

            // pull out what's waiting to go out over transport
            var _out = await local.DequeueToTransport(cts.Token);
            var response = CoapMessage.CreateFromBytes(_out.Payload);

            // we expect to pull out exactly what we put in.  Remember we only expect
            // incoming middleware to activate when endpoint *receives* data.  In this case,
            // we forced an endpoint *send*
            response.Id.Should().Be(m.Id);
            response.Type.Should().Be(CoapMessageType.Confirmable);

            cts.Cancel();
        }

        [Test]
        public async Task ClientTest3()
        {
            var cts = new CancellationTokenSource();
            var local = new SyntheticCoapEndpoint("coap://localhost");
            var remote = new SyntheticCoapEndpoint("coap://remotehost");
            
            var c = new CoapClient2(local, services, cts.Token);

            var m = CreateGetMessage();
            var ack = CreateAckMessage(m.Id);

            await c.SendAsync(m, remote);

            await local.EnqueueFromTransport(ack.ToPacket(remote));
        }
    }
}