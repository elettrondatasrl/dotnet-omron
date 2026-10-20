using System;
using RICADO.Omron.Channels;

namespace RICADO.Omron.Requests
{
    internal abstract class FINSRequest
    {

        internal const int HEADER_LENGTH = 10;
        internal const int COMMAND_LENGTH = 2;

        protected byte LocalNodeID { get; private set; }
        protected byte RemoteNodeID { get; private set; }
        protected byte[] Body;


        internal byte ServiceID { get; private set; }
        internal byte FunctionCode {  get; set; }
        internal byte SubFunctionCode { get; set; }

        protected FINSRequest(OmronPLC plc, byte functionCode, byte subFunctionCode)
        {
            if (plc.Channel is EthernetTCPChannel)
            {
                LocalNodeID = (plc.Channel as EthernetTCPChannel).LocalNodeID;
                RemoteNodeID = (plc.Channel as EthernetTCPChannel).RemoteNodeID;
            }
            else
            {
                LocalNodeID = plc.LocalNodeID;
                RemoteNodeID = plc.RemoteNodeID;
            }

            FunctionCode = functionCode;
            SubFunctionCode = subFunctionCode;
        }

        private byte[] Header()
        {
            var bytes = new byte[HEADER_LENGTH]
            {
                0x80,// Information Control Field
                0x00,// Reserved by System
                0x02,// Permissible Number of Gateways
                0x00,// Destination Network Address - 0 = Local Network
                RemoteNodeID,// Destination Node Address - 0 = Local PLC Unit - 1 to 254 = Destination Node Address - 255 = Broadcasting
                0x00,// Destination Unit Address - 0 = PLC (CPU Unit)
                0x00,// Source Network Address - 0 = Local Network
                LocalNodeID,// Source Node Address - Local Server
                0x00,// Source Unit Address
                ServiceID// Service ID
            };
            return bytes;
        }

        /// <summary>
        /// Builds the frame with the node addresses negotiated by the connection the frame travels on.
        /// After a reconnection the PLC may assign a different client node: the ones read when the
        /// request was created would belong to the old connection.
        /// </summary>
        internal byte[] BuildMessage(byte requestId, byte localNodeId, byte remoteNodeId)
        {
            LocalNodeID = localNodeId;
            RemoteNodeID = remoteNodeId;
            ServiceID = requestId;
            var bytes = new byte[HEADER_LENGTH + 2 + Body.Length];
            var header = Header();
            Array.Copy(header, 0, bytes, 0, HEADER_LENGTH);
            bytes[HEADER_LENGTH] = FunctionCode;// Main Function Code
            bytes[HEADER_LENGTH + 1] = SubFunctionCode;// Sub Function Code
            Array.Copy(Body, 0, bytes, HEADER_LENGTH + 2, Body.Length);// Request Data
            return bytes;
        }

    }
}
