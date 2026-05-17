using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text;

namespace UdpReceiver
{
    internal static class FlagEnumInfo<T> where T : unmanaged, Enum
    {
        public static readonly int Size;
        public static readonly Func<T, uint> ToUInt32;
        public static readonly Func<uint, T> FromUInt32;

        static FlagEnumInfo()
        {
            Size = Unsafe.SizeOf<T>();

            switch (Size)
            {
                case 1:
                    ToUInt32 = v => Unsafe.As<T, byte>(ref v);
                    FromUInt32 = v => (T)(object)(byte)v;
                    break;

                case 2:
                    ToUInt32 = v => Unsafe.As<T, ushort>(ref v);
                    FromUInt32 = v => (T)(object)(ushort)v;
                    break;

                case 4:
                    ToUInt32 = v => Unsafe.As<T, uint>(ref v);
                    FromUInt32 = v => (T)(object)v;
                    break;

                default:
                    throw new NotSupportedException();
            }
        }
    }

    internal static class ByteReader
    {
        public static int ReadI32(this byte[] buffer, int startIndex)
        {
            int val = (buffer[startIndex] |
                (buffer[startIndex + 1] << 8) |
                (buffer[startIndex + 2] << 16) |
                (buffer[startIndex + 3] << 24));

            return val;
        }

        public static uint ReadU32(this byte[] buffer, int startIndex)
        {
            return (uint)ReadI32(buffer, startIndex);
        }

        public static short ReadI16(this byte[] buffer, int startIndex)
        {
            short val = (short)(buffer[startIndex] | (buffer[startIndex + 1] << 8));
            return val;
        }

        public static ushort ReadU16(this byte[] buffer, int startIndex)
        {
            return (ushort)ReadI16(buffer, startIndex);
        }

        public static int ReadBytesSigned(this byte[] buffer, int startIndex, int length)
        {
            int ret = 0;

            for (int i = 0; i < length; i++)
            {
                ret |= buffer[startIndex + i] << (i * 8);
            }

            int shift = (4 - length) * 8;

            // C# has arithmetical right shift, meaning it will
            // replicate the sign bit on right shifts if it is 1.
            // So if we had length = 2 and bytes 0xFF 0xFF, we would have:
            // 0000 0000 0000 0000 1111 1111 1111 1111 << 16
            // = 1111 1111 1111 1111 0000 0000 0000 0000 >> 16
            //   ^-- rsh will replicate the sign bit!
            // = 1111 1111 1111 1111 1111 1111 1111 1111
            // If it were a logical rsh, we would have
            // 0000 0000 0000 0000 1111 1111 1111 1111 again.
            return (ret << shift) >> shift;
        }

        public static uint ReadBytesUnsigned(this byte[] buffer, int startIndex, int length)
        {
            uint ret = 0;

            for (int i = 0; i < length; i++)
            {
                ret |= (uint)buffer[startIndex + i] << (i * 8);
            }

            return ret;
        }

        public static float ReadF32(this byte[] buffer, int startIndex)
        {
            int val = buffer[startIndex]
                    | (buffer[startIndex + 1] << 8)
                    | (buffer[startIndex + 2] << 16)
                    | (buffer[startIndex + 3] << 24);

            return Unsafe.As<int, float>(ref val);
        }

        public static T ReadFlags<T>(this byte[] buffer, ref int offset) where T : unmanaged, Enum
        {
            int size = FlagEnumInfo<T>.Size;
            T value;

            switch (FlagEnumInfo<T>.Size)
            {
                case 1:
                    value = Unsafe.As<byte, T>(ref buffer[offset]);
                    break;
                case 2:
                    ushort v16 = buffer.ReadU16(offset);
                    value = Unsafe.As<ushort, T>(ref v16);
                    break;
                case 4:
                    uint v32 = buffer.ReadU32(offset);
                    value = Unsafe.As<uint, T>(ref v32);
                    break;
                case 8:
                    ulong v64 = BinaryPrimitives.ReadUInt64LittleEndian(buffer.AsSpan(offset));
                    value = Unsafe.As<ulong, T>(ref v64);
                    break;
                default:
                    throw new NotSupportedException();
            }

            offset += size;
            return value;
        }

    }

    internal struct FlagReader<T>(T flags) where T : unmanaged, Enum
    {
        private uint _flags = FlagEnumInfo<T>.ToUInt32(flags);

        public bool Next(out T flag)
        {
            if (_flags == 0)
            {
                flag = default;
                return false;
            }

            // Extract right-most set bit (BLSI).
            // -flag = ~flag + 1.
            // ~flag turns all zeros to the right of first set bit to 1, and the first set bit to 0.
            // Adding 1 will make all the bits that were set to 1 turn to 0 and set the bit
            // to the left of them, i.e. the first set bit previously, to 1.
            // At this point, everything to the right of this set bit is 0 again, and everything
            // to the left of it is the inverse of what they were before.
            // Which means ANDing this result with the original number will make only 1 bit remain.
            // 0110 1000 -> ~ -> 1001 0111 -> +1 -> 1001 1000
            //     0110 1000 (original)
            // AND 1001 1000
            // --> 0000 1000
            uint next = _flags & (uint)-(int)_flags;

            // bitmask - 1 will turn the set bit to 0,
            // and all other bits to right to 1.
            // ANDing this number with the original bitmask
            // (which is 1 on the bit index and all 0 to the right)
            // will result in zeroing the current set bit along with
            // everything else to the right of it.
            //     mask = 1100 0000
            // mask - 1 = 1011 1111
            //      AND = 1000 0000 <- next time the set bit index
            //                         will be the next possessed WP
            _flags &= _flags - 1;

            flag = FlagEnumInfo<T>.FromUInt32(next);
            return true;
        }
    }

    internal ref struct PacketReader
    {
        private readonly ReadOnlySpan<byte> _data;
        private int _offset;

        public readonly int Offset => _offset;
        public readonly int Remaining => _data.Length - _offset;

        public PacketReader(ReadOnlySpan<byte> data)
        {
            _data = data;
            _offset = 0;
        }

        // --- primitive reads ---

        public byte ReadU8()
        {
            return _data[_offset++];
        }

        public sbyte ReadI8()
        {
            return (sbyte)_data[_offset++];
        }

        public ushort ReadU16()
        {
            ushort v = BinaryPrimitives.ReadUInt16LittleEndian(_data.Slice(_offset));
            _offset += 2;
            return v;
        }

        public short ReadI16()
        {
            short v = BinaryPrimitives.ReadInt16LittleEndian(_data.Slice(_offset));
            _offset += 2;
            return v;
        }

        public uint ReadU32()
        {
            uint v = BinaryPrimitives.ReadUInt32LittleEndian(_data.Slice(_offset));
            _offset += 4;
            return v;
        }

        public float ReadF32()
        {
            uint raw = ReadU32();
            return Unsafe.As<uint, float>(ref raw);
        }

        public string ReadString()
        {
            int length = ReadU8();
            var str = Encoding.UTF8.GetString(_data.Slice(_offset, length));
            _offset += length;

            return str;
        }

        public T ReadFlags<T>() where T : unmanaged, Enum
        {
            int size = FlagEnumInfo<T>.Size;

            T value;

            if (size == 1)
            {
                // value = (T)(object)_data[_offset];
                byte v = _data[_offset++];
                return Unsafe.As<byte, T>(ref v);
            }
            else if (size == 2)
            {
                ushort v = ReadU16();
                value = Unsafe.As<ushort, T>(ref v);
                return value;
            }
            else if (size == 4)
            {
                uint v = ReadU32();
                value = Unsafe.As<uint, T>(ref v);
                return value;
            }
            else
            {
                throw new NotSupportedException();
            }

            //_offset += size;
            //return value;
        }

    }

    internal class GlobalDelta
    {
        public GlobalFlags Flags = 0;

        public float? RoundEndTick;
        public byte? TScore;
        public byte? CTScore;
        public string? Map;
    }

    internal class PlayerDelta
    {
        public PlayerFlags Flags = 0;

        public byte Id;

        public Team? Team;
        public float? Yaw;
        public (short x, short y, short z)? Pos;
        public sbyte? Hp;
        public (ArmorType ArmorType, byte ArmorValue)? Armor;
        public WeaponId? CurrentWeapon;
        public ushort? Money;
        public sbyte? Frags;
        public byte? Deaths;

        // --- Translated inventory ---
        public WeaponId? PrimaryWeapon;
        public WeaponId? SecondaryWeapon;
        public Grenades? Grenades;
        public bool? HasC4;

        public ItemsHeld? Items;
        public string? Name;

        public bool? Dropped;

        public bool HasInventory =>
        PrimaryWeapon.HasValue &&
        SecondaryWeapon.HasValue &&
        Grenades.HasValue &&
        HasC4.HasValue;
    }
}
