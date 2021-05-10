using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace CoAPNet.Middleware.Tests
{
	public class SyntheticCoapEndpoint : ICoapEndpoint
	{
		public bool IsSecure => throw new NotImplementedException();

		public bool IsMulticast => throw new NotImplementedException();

		public Uri BaseUri { get; set; }


		public Channel<CoapPacket> channel = Channel.CreateUnbounded<CoapPacket>();

		public void Dispose()
		{
			throw new NotImplementedException();
		}

		public SyntheticCoapEndpoint(string uri)
		{
			BaseUri = new Uri(uri);
		}

		public async Task<CoapPacket> ReceiveAsync(CancellationToken tokens)
		{
			return await channel.Reader.ReadAsync(tokens);
		}

		public async Task SendAsync(CoapPacket packet, CancellationToken token)
		{
			await channel.Writer.WriteAsync(packet);
		}

		public string ToString(CoapEndpointStringFormat format)
		{
			return BaseUri.ToString();
		}
	}


	public class SyntheticMiddleware : ICoapMiddleware
	{
		public async Task Invoke(CoapContext context)
		{
			var m = new CoapMessage
			{
				Id = context.Message.Id,
				Token = context.Message.Token,
				Type = CoapMessageType.Acknowledgement
			};
			CoapPacket p = m.ToPacket(context.Connection.RemoteEndpoint);
			await context.Connection.LocalEndpoint.SendAsync(p, context.CancellationToken);
		}
	}
}
