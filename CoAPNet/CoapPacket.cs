﻿using System.Collections.Generic;

namespace CoAPNet
{
    public class CoapPacket
    {
        public virtual int MessageId { get; set; }

        public virtual byte[] Payload { get; set; }

        public virtual ICoapEndpoint Endpoint { get; set; }
    }
}