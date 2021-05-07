using System;
using System.Collections.Generic;
using System.Text;

namespace CoAPNet.Middleware
{
	public interface ICoapObserver
	{
		CoapMessage InitialMessage { get; }

		ICoapEndpoint RemoteEndpoint { get; }

		/// <summary>
		/// DEBT: We may want to not track this per observer, though it's helpful to do so
		/// It makes multi-homing easier
		/// </summary>
		ICoapEndpoint LocalEndpoint { get; }
	}

	public interface ICoapSubject
	{
		event Action<ICoapObserver> Register;
		event Action<ICoapObserver> Deregister;
	}


	namespace Options
	{
		public class Observe : CoAPNet.CoapUintOption
		{
			public Observe() : base(6, 0, 3
				//, defaultValue: 0
				)
			{ }

			public Observe(uint value) : this()
			{
				ValueUInt = value;
			}
		}
	}

}
