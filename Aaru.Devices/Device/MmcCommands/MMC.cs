// /***************************************************************************
// Aaru Data Preservation Suite
// ----------------------------------------------------------------------------
//
// Filename       : MMC.cs
// Author(s)      : Natalia Portillo <claunia@claunia.com>
//
// Component      : SecureDigital and MultiMediaCard commands.
//
// --[ Description ] ----------------------------------------------------------
//
//     Contains MultiMediaCard commands.
//
// --[ License ] --------------------------------------------------------------
//
//     This library is free software; you can redistribute it and/or modify
//     it under the terms of the GNU Lesser General Public License as
//     published by the Free Software Foundation; either version 2.1 of the
//     License, or (at your option) any later version.
//
//     This library is distributed in the hope that it will be useful, but
//     WITHOUT ANY WARRANTY; without even the implied warranty of
//     MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the GNU
//     Lesser General Public License for more details.
//
//     You should have received a copy of the GNU Lesser General Public
//     License along with this library; if not, see <http://www.gnu.org/licenses/>.
//
// ----------------------------------------------------------------------------
// Copyright © 2011-2020 Natalia Portillo
// ****************************************************************************/

using System;
using Aaru.Console;

namespace Aaru.Devices
{
    public sealed partial class Device
    {
        public bool ReadCsd(out byte[] buffer, out uint[] response, uint timeout, out double duration)
        {
            buffer = new byte[16];

            LastError = SendMmcCommand(MmcCommands.SendCsd, false, false,
                                       MmcFlags.ResponseSpiR2 | MmcFlags.ResponseR2 | MmcFlags.CommandAc, 0, 16, 1,
                                       ref buffer, out response, out duration, out bool sense, timeout);

            Error = LastError != 0;

            AaruConsole.DebugWriteLine("MMC Device", "SEND_CSD took {0} ms.", duration);

            return sense;
        }

        public bool ReadCid(out byte[] buffer, out uint[] response, uint timeout, out double duration)
        {
            buffer = new byte[16];

            LastError = SendMmcCommand(MmcCommands.SendCid, false, false,
                                       MmcFlags.ResponseSpiR2 | MmcFlags.ResponseR2 | MmcFlags.CommandAc, 0, 16, 1,
                                       ref buffer, out response, out duration, out bool sense, timeout);

            Error = LastError != 0;

            AaruConsole.DebugWriteLine("MMC Device", "SEND_CID took {0} ms.", duration);

            return sense;
        }

        public bool ReadOcr(out byte[] buffer, out uint[] response, uint timeout, out double duration)
        {
            buffer = new byte[4];

            LastError = SendMmcCommand(MmcCommands.SendOpCond, false, true,
                                       MmcFlags.ResponseSpiR3 | MmcFlags.ResponseR3 | MmcFlags.CommandBcr, 0, 4, 1,
                                       ref buffer, out response, out duration, out bool sense, timeout);

            Error = LastError != 0;

            AaruConsole.DebugWriteLine("SecureDigital Device", "SEND_OP_COND took {0} ms.", duration);

            return sense;
        }

        public bool ReadExtendedCsd(out byte[] buffer, out uint[] response, uint timeout, out double duration)
        {
            buffer = new byte[512];

            LastError = SendMmcCommand(MmcCommands.SendExtCsd, false, false,
                                       MmcFlags.ResponseSpiR1 | MmcFlags.ResponseR1 | MmcFlags.CommandAdtc, 0, 512, 1,
                                       ref buffer, out response, out duration, out bool sense, timeout);

            Error = LastError != 0;

            AaruConsole.DebugWriteLine("MMC Device", "SEND_EXT_CSD took {0} ms.", duration);

            return sense;
        }

        public bool SetBlockLength(uint length, out uint[] response, uint timeout, out double duration)
        {
            byte[] buffer = new byte[0];

            LastError = SendMmcCommand(MmcCommands.SetBlocklen, false, false,
                                       MmcFlags.ResponseSpiR1 | MmcFlags.ResponseR1 | MmcFlags.CommandAc, length, 0, 0,
                                       ref buffer, out response, out duration, out bool sense, timeout);

            Error = LastError != 0;

            AaruConsole.DebugWriteLine("MMC Device", "SET_BLOCKLEN took {0} ms.", duration);

            return sense;
        }

        public bool Read(out byte[] buffer, out uint[] response, uint lba, uint blockSize, ushort transferLength,
                         bool byteAddressed, uint timeout, out double duration)
        {
            bool sense = true;
            buffer   = null;
            response = null;
            duration = -1;

            if(transferLength <= 1)
                return ReadSingleBlock(out buffer, out response, lba, blockSize, byteAddressed, timeout, out duration);

            if(!_readMultipleBlockCannotSetBlockCount)
                sense = ReadMultipleBlock(out buffer, out response, lba, blockSize, transferLength, byteAddressed,
                                          timeout, out duration);

            if(_readMultipleBlockCannotSetBlockCount)
                return ReadMultipleUsingSingle(out buffer, out response, lba, blockSize, transferLength, byteAddressed,
                                               timeout, out duration);

            return sense;
        }

        public bool ReadSingleBlock(out byte[] buffer, out uint[] response, uint lba, uint blockSize,
                                    bool byteAddressed, uint timeout, out double duration)
        {
            uint address;
            buffer   = new byte[blockSize];
            response = null;

            if(byteAddressed)
                address = lba * blockSize;
            else
                address = lba;

            LastError = SendMmcCommand(MmcCommands.ReadSingleBlock, false, false,
                                       MmcFlags.ResponseSpiR1 | MmcFlags.ResponseR1 | MmcFlags.CommandAdtc, address,
                                       blockSize, 1, ref buffer, out response, out duration, out bool sense, timeout);

            Error = LastError != 0;

            AaruConsole.DebugWriteLine("MMC Device", "READ_SINGLE_BLOCK took {0} ms.", duration);

            return sense;
        }

        static bool _readMultipleBlockCannotSetBlockCount;

        public bool ReadMultipleBlock(out byte[] buffer, out uint[] response, uint lba, uint blockSize,
                                      ushort transferLength, bool byteAddressed, uint timeout, out double duration)
        {
            buffer = new byte[transferLength * blockSize];
            byte[] foo         = new byte[0];
            double setDuration = 0;
            bool   sense;
            uint   address;
            response = null;

            if(byteAddressed)
                address = lba * blockSize;
            else
                address = lba;

            if(transferLength > 1)
            {
                LastError = SendMmcCommand(MmcCommands.SetBlockCount, false, false,
                                           MmcFlags.ResponseSpiR1 | MmcFlags.ResponseR1 | MmcFlags.CommandAc,
                                           transferLength, 0, 0, ref foo, out _, out setDuration, out sense, timeout);

                Error = LastError != 0;

                if(Error || sense)
                {
                    duration = setDuration;

                    return sense;
                }
            }

            LastError = SendMmcCommand(MmcCommands.ReadMultipleBlock, false, false,
                                       MmcFlags.ResponseSpiR1 | MmcFlags.ResponseR1 | MmcFlags.CommandAdtc, address,
                                       blockSize, transferLength, ref buffer, out response, out duration, out sense,
                                       timeout);

            Error = LastError != 0;

            // Seems that SET_BLOCK_COUNT followed by READ_MULTIPLE_BLOCK is not atomic in Linux and is giving an error status.
            // TODO: Check Windows
            if(LastError == 110)
            {
                SendMmcCommand(MmcCommands.StopTransmission, false, false,
                               MmcFlags.ResponseSpiR1 | MmcFlags.ResponseR1 | MmcFlags.CommandAc, 0, 0, 0, ref foo,
                               out _, out _, out _, timeout);

                _readMultipleBlockCannotSetBlockCount = true;
            }

            if(transferLength > 1)
            {
                duration += setDuration;
                AaruConsole.DebugWriteLine("MMC Device", "READ_MULTIPLE_BLOCK took {0} ms.", duration);
            }
            else
                AaruConsole.DebugWriteLine("MMC Device", "READ_SINGLE_BLOCK took {0} ms.", duration);

            return sense;
        }

        bool ReadMultipleUsingSingle(out byte[] buffer, out uint[] response, uint lba, uint blockSize,
                                     ushort transferLength, bool byteAddressed, uint timeout, out double duration)
        {
            buffer = new byte[transferLength * blockSize];
            byte[] blockBuffer = new byte[blockSize];
            duration = 0;
            bool sense = true;
            response = null;

            for(uint i = 0; i < transferLength; i++)
            {
                uint address;

                if(byteAddressed)
                    address = (lba + i) * blockSize;
                else
                    address = lba + i;

                LastError = SendMmcCommand(MmcCommands.ReadSingleBlock, false, false,
                                           MmcFlags.ResponseSpiR1 | MmcFlags.ResponseR1 | MmcFlags.CommandAdtc, address,
                                           blockSize, 1, ref blockBuffer, out response, out double blockDuration,
                                           out sense, timeout);

                Error = LastError != 0;

                duration += blockDuration;

                if(Error || sense)
                    break;

                Array.Copy(blockBuffer, 0, buffer, i * blockSize, blockSize);
            }

            AaruConsole.DebugWriteLine("MMC Device", "Multiple READ_SINGLE_BLOCKs took {0} ms.", duration);

            return sense;
        }

        public bool ReadStatus(out byte[] buffer, out uint[] response, uint timeout, out double duration)
        {
            buffer = new byte[4];

            LastError = SendMmcCommand(MmcCommands.SendStatus, false, true,
                                       MmcFlags.ResponseSpiR1 | MmcFlags.ResponseR1 | MmcFlags.CommandAc, 0, 4, 1,
                                       ref buffer, out response, out duration, out bool sense, timeout);

            Error = LastError != 0;

            AaruConsole.DebugWriteLine("SecureDigital Device", "SEND_STATUS took {0} ms.", duration);

            return sense;
        }
    }
}