using System.Net;
using System.Net.Sockets;

namespace TinyCosmos.Linux;

public sealed class VsockEndPoint : EndPoint
{
    public const int AddressFamilyValue = 40;
    public const uint CidAny = 0xffffffff;
    public const uint CidHost = 2;

    private const int SockAddrVmLength = 16;
    private readonly uint _cid;
    private readonly uint _port;

    public VsockEndPoint(uint cid, uint port)
    {
        _cid = cid;
        _port = port;
    }

    public uint Cid => _cid;

    public uint Port => _port;

    public override AddressFamily AddressFamily => (AddressFamily)AddressFamilyValue;

    public override SocketAddress Serialize()
    {
        var address = new SocketAddress(AddressFamily, SockAddrVmLength);
        WriteUInt16(address, 0, AddressFamilyValue);
        WriteUInt16(address, 2, 0);
        WriteUInt32(address, 4, _port);
        WriteUInt32(address, 8, _cid);
        return address;
    }

    public override EndPoint Create(SocketAddress socketAddress)
    {
        if (socketAddress.Size < SockAddrVmLength || (int)socketAddress.Family != AddressFamilyValue)
        {
            throw new ArgumentException("Socket address is not an AF_VSOCK sockaddr_vm.", nameof(socketAddress));
        }

        return new VsockEndPoint(ReadUInt32(socketAddress, 8), ReadUInt32(socketAddress, 4));
    }

    public override string ToString() => $"vsock:{_cid}:{_port}";

    private static void WriteUInt16(SocketAddress address, int offset, int value)
    {
        address[offset] = (byte)(value & 0xff);
        address[offset + 1] = (byte)((value >> 8) & 0xff);
    }

    private static void WriteUInt32(SocketAddress address, int offset, uint value)
    {
        address[offset] = (byte)(value & 0xff);
        address[offset + 1] = (byte)((value >> 8) & 0xff);
        address[offset + 2] = (byte)((value >> 16) & 0xff);
        address[offset + 3] = (byte)((value >> 24) & 0xff);
    }

    private static uint ReadUInt32(SocketAddress address, int offset)
    {
        return (uint)(address[offset] |
            (address[offset + 1] << 8) |
            (address[offset + 2] << 16) |
            (address[offset + 3] << 24));
    }
}
