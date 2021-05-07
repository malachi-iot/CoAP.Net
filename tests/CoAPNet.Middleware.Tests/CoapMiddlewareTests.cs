using NUnit.Framework;
using System;
using System.Threading;

namespace CoAPNet.Middleware.Tests
{
    public class CoapMiddlewareTests
    {
        [SetUp]
        public void Setup()
        {
        }

        [Test]
        public void TestRetry1()
        {
            var dtReceived = DateTimeOffset.Now;
            var conn = new CoapConnectionInformation(null, null);
            var cts = new CancellationTokenSource();
            var c = new CoapContext(conn, null, dtReceived, null, cts.Token);
            //var m = new CoapRetryMiddleware(c, null);

            Assert.Pass();
        }
    }
}