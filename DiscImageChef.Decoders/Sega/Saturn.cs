// /***************************************************************************
// The Disc Image Chef
// ----------------------------------------------------------------------------
//
// Filename       : Saturn.cs
// Author(s)      : Natalia Portillo <claunia@claunia.com>
//
// Component      : Device structures decoders.
//
// --[ Description ] ----------------------------------------------------------
//
//     Decodes Sega Saturn IP.BIN.
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
// Copyright © 2011-2018 Natalia Portillo
// ****************************************************************************/

using System;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using DiscImageChef.Console;

namespace DiscImageChef.Decoders.Sega
{
    [SuppressMessage("ReSharper", "InconsistentNaming")]
    [SuppressMessage("ReSharper", "MemberCanBeInternal")]
    [SuppressMessage("ReSharper", "MemberCanBePrivate.Global")]
    public static class Saturn
    {
        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        public struct IPBin
        {
            /// <summary>Must be "SEGA SEGASATURN "</summary>
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] public byte[] SegaHardwareID;
            /// <summary>0x010, "SEGA ENTERPRISES"</summary>
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] public byte[] maker_id;
            /// <summary>0x020, Product number</summary>
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 10)] public byte[] product_no;
            /// <summary>0x02A, Product version</summary>
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 6)] public byte[] product_version;
            /// <summary>0x030, YYYYMMDD</summary>
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)] public byte[] release_date;
            /// <summary>0x038, "CD-"</summary>
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 3)] public byte[] saturn_media;
            /// <summary>0x03B, Disc number</summary>
            public byte disc_no;
            /// <summary>// 0x03C, '/'</summary>
            public byte disc_no_separator;
            /// <summary>// 0x03D, Total number of discs</summary>
            public byte disc_total_nos;
            /// <summary>0x03E, "  "</summary>
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 2)] public byte[] spare_space1;
            /// <summary>0x040, Region codes, space-filled</summary>
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] public byte[] region_codes;
            /// <summary>0x050, Supported peripherals, see above</summary>
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] public byte[] peripherals;
            /// <summary>0x060, Game name, space-filled</summary>
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 112)] public byte[] product_name;
        }

        public static IPBin? DecodeIPBin(byte[] ipbin_sector)
        {
            if(ipbin_sector == null) return null;

            if(ipbin_sector.Length < 512) return null;

            IntPtr ptr = Marshal.AllocHGlobal(512);
            Marshal.Copy(ipbin_sector, 0, ptr, 512);
            IPBin ipbin = (IPBin)Marshal.PtrToStructure(ptr, typeof(IPBin));
            Marshal.FreeHGlobal(ptr);

            DicConsole.DebugWriteLine("ISO9660 plugin", "saturn_ipbin.maker_id = \"{0}\"",
                                      Encoding.ASCII.GetString(ipbin.maker_id));
            DicConsole.DebugWriteLine("ISO9660 plugin", "saturn_ipbin.product_no = \"{0}\"",
                                      Encoding.ASCII.GetString(ipbin.product_no));
            DicConsole.DebugWriteLine("ISO9660 plugin", "saturn_ipbin.product_version = \"{0}\"",
                                      Encoding.ASCII.GetString(ipbin.product_version));
            DicConsole.DebugWriteLine("ISO9660 plugin", "saturn_ipbin.release_datedate = \"{0}\"",
                                      Encoding.ASCII.GetString(ipbin.release_date));
            DicConsole.DebugWriteLine("ISO9660 plugin", "saturn_ipbin.saturn_media = \"{0}\"",
                                      Encoding.ASCII.GetString(ipbin.saturn_media));
            DicConsole.DebugWriteLine("ISO9660 plugin", "saturn_ipbin.disc_no = {0}", (char)ipbin.disc_no);
            DicConsole.DebugWriteLine("ISO9660 plugin", "saturn_ipbin.disc_no_separator = \"{0}\"",
                                      (char)ipbin.disc_no_separator);
            DicConsole.DebugWriteLine("ISO9660 plugin", "saturn_ipbin.disc_total_nos = {0}",
                                      (char)ipbin.disc_total_nos);
            DicConsole.DebugWriteLine("ISO9660 plugin", "saturn_ipbin.release_date = \"{0}\"",
                                      Encoding.ASCII.GetString(ipbin.release_date));
            DicConsole.DebugWriteLine("ISO9660 plugin", "saturn_ipbin.spare_space1 = \"{0}\"",
                                      Encoding.ASCII.GetString(ipbin.spare_space1));
            DicConsole.DebugWriteLine("ISO9660 plugin", "saturn_ipbin.region_codes = \"{0}\"",
                                      Encoding.ASCII.GetString(ipbin.region_codes));
            DicConsole.DebugWriteLine("ISO9660 plugin", "saturn_ipbin.peripherals = \"{0}\"",
                                      Encoding.ASCII.GetString(ipbin.peripherals));
            DicConsole.DebugWriteLine("ISO9660 plugin", "saturn_ipbin.product_name = \"{0}\"",
                                      Encoding.ASCII.GetString(ipbin.product_name));

            return Encoding.ASCII.GetString(ipbin.SegaHardwareID) == "SEGA SEGASATURN " ? ipbin : (IPBin?)null;
        }

        public static string Prettify(IPBin? decoded)
        {
            if(decoded == null) return null;

            IPBin ipbin = decoded.Value;

            StringBuilder IPBinInformation = new StringBuilder();

            IPBinInformation.AppendLine("--------------------------------");
            IPBinInformation.AppendLine("SEGA IP.BIN INFORMATION:");
            IPBinInformation.AppendLine("--------------------------------");

            // Decoding all data
            DateTime ipbindate;
            CultureInfo provider = CultureInfo.InvariantCulture;
            ipbindate = DateTime.ParseExact(Encoding.ASCII.GetString(ipbin.release_date), "yyyyMMdd", provider);
            IPBinInformation.AppendFormat("Product name: {0}", Encoding.ASCII.GetString(ipbin.product_name))
                            .AppendLine();
            IPBinInformation.AppendFormat("Product number: {0}", Encoding.ASCII.GetString(ipbin.product_no))
                            .AppendLine();
            IPBinInformation.AppendFormat("Product version: {0}", Encoding.ASCII.GetString(ipbin.product_version))
                            .AppendLine();
            IPBinInformation.AppendFormat("Release date: {0}", ipbindate).AppendLine();
            IPBinInformation.AppendFormat("Disc number {0} of {1}", (char)ipbin.disc_no, (char)ipbin.disc_total_nos)
                            .AppendLine();

            IPBinInformation.AppendFormat("Peripherals:").AppendLine();
            foreach(byte peripheral in ipbin.peripherals)
                switch((char)peripheral)
                {
                    case 'A':
                        IPBinInformation.AppendLine("Game supports analog controller.");
                        break;
                    case 'J':
                        IPBinInformation.AppendLine("Game supports JoyPad.");
                        break;
                    case 'K':
                        IPBinInformation.AppendLine("Game supports keyboard.");
                        break;
                    case 'M':
                        IPBinInformation.AppendLine("Game supports mouse.");
                        break;
                    case 'S':
                        IPBinInformation.AppendLine("Game supports analog steering controller.");
                        break;
                    case 'T':
                        IPBinInformation.AppendLine("Game supports multitap.");
                        break;
                    case ' ': break;
                    default:
                        IPBinInformation.AppendFormat("Game supports unknown peripheral {0}.", peripheral).AppendLine();
                        break;
                }

            IPBinInformation.AppendLine("Regions supported:");
            foreach(byte region in ipbin.region_codes)
                switch((char)region)
                {
                    case 'J':
                        IPBinInformation.AppendLine("Japanese NTSC.");
                        break;
                    case 'U':
                        IPBinInformation.AppendLine("North America NTSC.");
                        break;
                    case 'E':
                        IPBinInformation.AppendLine("Europe PAL.");
                        break;
                    case 'T':
                        IPBinInformation.AppendLine("Asia NTSC.");
                        break;
                    case ' ': break;
                    default:
                        IPBinInformation.AppendFormat("Game supports unknown region {0}.", region).AppendLine();
                        break;
                }

            return IPBinInformation.ToString();
        }
    }
}