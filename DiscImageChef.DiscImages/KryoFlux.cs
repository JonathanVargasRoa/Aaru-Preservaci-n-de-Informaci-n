// /***************************************************************************
// The Disc Image Chef
// ----------------------------------------------------------------------------
//
// Filename       : KryoFlux.cs
// Author(s)      : Natalia Portillo <claunia@claunia.com>
//
// Component      : Disk image plugins.
//
// --[ Description ] ----------------------------------------------------------
//
//     Manages KryoFlux STREAM images.
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
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using DiscImageChef.CommonTypes;
using DiscImageChef.Console;
using DiscImageChef.Filters;
using Schemas;

namespace DiscImageChef.DiscImages
{
    [SuppressMessage("ReSharper", "InconsistentNaming")]
    public class KryoFlux : IMediaImage
    {
        const string hostDate  = "host_date";
        const string hostTime  = "host_time";
        const string kfName    = "name";
        const string kfVersion = "version";
        const string kfDate    = "date";
        const string kfTime    = "time";
        const string kfHwId    = "hwid";
        const string kfHwRv    = "hwrv";
        const string kfSck     = "sck";
        const string kfIck     = "ick";

        // TODO: These variables have been made public so create-sidecar can access to this information until I define an API >4.0
        public SortedDictionary<byte, IFilter> tracks;
        public ImageInfo                       imageInfo;
        public ImageInfo                       Info => imageInfo;

        public string Name => "KryoFlux STREAM";
        public Guid   Id   => new Guid("4DBC95E4-93EE-4F7A-9492-919887E60EFE");

        public KryoFlux()
        {
            imageInfo = new ImageInfo
            {
                ReadableSectorTags    = new List<SectorTagType>(),
                ReadableMediaTags     = new List<MediaTagType>(),
                HasPartitions         = false,
                HasSessions           = false,
                Version               = null,
                Application           = null,
                ApplicationVersion    = null,
                Creator               = null,
                Comments              = null,
                MediaManufacturer     = null,
                MediaModel            = null,
                MediaSerialNumber     = null,
                MediaBarcode          = null,
                MediaPartNumber       = null,
                MediaSequence         = 0,
                LastMediaSequence     = 0,
                DriveManufacturer     = null,
                DriveModel            = null,
                DriveSerialNumber     = null,
                DriveFirmwareRevision = null
            };
        }

        public bool Identify(IFilter imageFilter)
        {
            OobBlock header = new OobBlock();
            Stream   stream = imageFilter.GetDataForkStream();
            stream.Seek(0, SeekOrigin.Begin);
            if(stream.Length < Marshal.SizeOf(header)) return false;

            byte[] hdr = new byte[Marshal.SizeOf(header)];
            stream.Read(hdr, 0, Marshal.SizeOf(header));

            IntPtr hdrPtr = Marshal.AllocHGlobal(Marshal.SizeOf(header));
            Marshal.Copy(hdr, 0, hdrPtr, Marshal.SizeOf(header));
            header = (OobBlock)Marshal.PtrToStructure(hdrPtr, typeof(OobBlock));
            Marshal.FreeHGlobal(hdrPtr);

            OobBlock footer = new OobBlock();
            stream.Seek(-Marshal.SizeOf(footer), SeekOrigin.End);

            hdr = new byte[Marshal.SizeOf(footer)];
            stream.Read(hdr, 0, Marshal.SizeOf(footer));

            hdrPtr = Marshal.AllocHGlobal(Marshal.SizeOf(footer));
            Marshal.Copy(hdr, 0, hdrPtr, Marshal.SizeOf(footer));
            footer = (OobBlock)Marshal.PtrToStructure(hdrPtr, typeof(OobBlock));
            Marshal.FreeHGlobal(hdrPtr);

            return header.blockId == BlockIds.Oob && header.blockType == OobTypes.KFInfo &&
                   footer.blockId == BlockIds.Oob && footer.blockType == OobTypes.EOF    && footer.length == 0x0D0D;
        }

        public bool Open(IFilter imageFilter)
        {
            OobBlock header = new OobBlock();
            Stream   stream = imageFilter.GetDataForkStream();
            stream.Seek(0, SeekOrigin.Begin);
            if(stream.Length < Marshal.SizeOf(header)) return false;

            byte[] hdr = new byte[Marshal.SizeOf(header)];
            stream.Read(hdr, 0, Marshal.SizeOf(header));

            IntPtr hdrPtr = Marshal.AllocHGlobal(Marshal.SizeOf(header));
            Marshal.Copy(hdr, 0, hdrPtr, Marshal.SizeOf(header));
            header = (OobBlock)Marshal.PtrToStructure(hdrPtr, typeof(OobBlock));
            Marshal.FreeHGlobal(hdrPtr);

            OobBlock footer = new OobBlock();
            stream.Seek(-Marshal.SizeOf(footer), SeekOrigin.End);

            hdr = new byte[Marshal.SizeOf(footer)];
            stream.Read(hdr, 0, Marshal.SizeOf(footer));

            hdrPtr = Marshal.AllocHGlobal(Marshal.SizeOf(footer));
            Marshal.Copy(hdr, 0, hdrPtr, Marshal.SizeOf(footer));
            footer = (OobBlock)Marshal.PtrToStructure(hdrPtr, typeof(OobBlock));
            Marshal.FreeHGlobal(hdrPtr);

            if(header.blockId != BlockIds.Oob || header.blockType != OobTypes.KFInfo ||
               footer.blockId != BlockIds.Oob || footer.blockType != OobTypes.EOF    ||
               footer.length  != 0x0D0D) return false;

            // TODO: This is supposing NoFilter, shouldn't
            tracks          = new SortedDictionary<byte, IFilter>();
            byte   step     = 1;
            byte   heads    = 2;
            bool   topHead  = false;
            string basename = Path.Combine(imageFilter.GetParentFolder(),
                                           imageFilter.GetFilename()
                                                      .Substring(0, imageFilter.GetFilename().Length - 8));

            for(byte t = 0; t < 166; t += step)
            {
                int    cylinder  = t               / heads;
                int    head      = topHead ? 1 : t % heads;
                string trackfile = Directory.Exists(basename)
                                       ? Path.Combine(basename, $"{cylinder:D2}.{head:D1}.raw")
                                       : $"{basename}{cylinder:D2}.{head:D1}.raw";

                if(!File.Exists(trackfile))
                    if(cylinder == 0)
                    {
                        if(head == 0)
                        {
                            DicConsole.DebugWriteLine("KryoFlux plugin",
                                                      "Cannot find cyl 0 hd 0, supposing only top head was dumped");
                            topHead = true;
                            heads   = 1;
                            continue;
                        }

                        DicConsole.DebugWriteLine("KryoFlux plugin",
                                                  "Cannot find cyl 0 hd 1, supposing only bottom head was dumped");
                        heads = 1;
                        continue;
                    }
                    else if(cylinder == 1)
                    {
                        DicConsole.DebugWriteLine("KryoFlux plugin", "Cannot find cyl 1, supposing double stepping");
                        step = 2;
                        continue;
                    }
                    else
                    {
                        DicConsole.DebugWriteLine("KryoFlux plugin", "Arrived end of disk at cylinder {0}", cylinder);
                        break;
                    }

                ZZZNoFilter trackFilter = new ZZZNoFilter();
                trackFilter.Open(trackfile);
                if(!trackFilter.IsOpened()) throw new IOException("Could not open KryoFlux track file.");

                imageInfo.CreationTime         = DateTime.MaxValue;
                imageInfo.LastModificationTime = DateTime.MinValue;

                Stream trackStream = trackFilter.GetDataForkStream();
                while(trackStream.Position < trackStream.Length)
                {
                    byte blockId = (byte)trackStream.ReadByte();
                    switch(blockId)
                    {
                        case (byte)BlockIds.Oob:
                        {
                            trackStream.Position--;
                            OobBlock oobBlk = new OobBlock();

                            byte[] oob = new byte[Marshal.SizeOf(oobBlk)];
                            trackStream.Read(oob, 0, Marshal.SizeOf(oobBlk));

                            IntPtr oobPtr = Marshal.AllocHGlobal(Marshal.SizeOf(oobBlk));
                            Marshal.Copy(oob, 0, oobPtr, Marshal.SizeOf(oobBlk));
                            oobBlk = (OobBlock)Marshal.PtrToStructure(oobPtr, typeof(OobBlock));
                            Marshal.FreeHGlobal(oobPtr);

                            if(oobBlk.blockType == OobTypes.EOF)
                            {
                                trackStream.Position = trackStream.Length;
                                break;
                            }

                            if(oobBlk.blockType != OobTypes.KFInfo)
                            {
                                trackStream.Position += oobBlk.length;
                                break;
                            }

                            byte[] kfinfo = new byte[oobBlk.length];
                            trackStream.Read(kfinfo, 0, oobBlk.length);
                            string   kfinfoStr = StringHandlers.CToString(kfinfo);
                            string[] lines     = kfinfoStr.Split(new[] {','}, StringSplitOptions.RemoveEmptyEntries);

                            DateTime blockDate = DateTime.Now;
                            DateTime blockTime = DateTime.Now;
                            bool     foundDate = false;

                            foreach(string[] kvp in lines.Select(line => line.Split('=')).Where(kvp => kvp.Length == 2))
                            {
                                kvp[0] = kvp[0].Trim();
                                kvp[1] = kvp[1].Trim();
                                DicConsole.DebugWriteLine("KryoFlux plugin", "\"{0}\" = \"{1}\"", kvp[0], kvp[1]);

                                switch(kvp[0])
                                {
                                    case hostDate:
                                        if(DateTime.TryParseExact(kvp[1], "yyyy.MM.dd", CultureInfo.InvariantCulture,
                                                                  DateTimeStyles.AssumeLocal, out blockDate))
                                            foundDate = true;
                                        break;
                                    case hostTime:
                                        DateTime.TryParseExact(kvp[1], "HH:mm:ss", CultureInfo.InvariantCulture,
                                                               DateTimeStyles.AssumeLocal, out blockTime);
                                        break;
                                    case kfName:
                                        imageInfo.Application = kvp[1];
                                        break;
                                    case kfVersion:
                                        imageInfo.ApplicationVersion = kvp[1];
                                        break;
                                }
                            }

                            if(foundDate)
                            {
                                DateTime blockTimestamp = new DateTime(blockDate.Year, blockDate.Month, blockDate.Day,
                                                                       blockTime.Hour, blockTime.Minute,
                                                                       blockTime.Second);
                                DicConsole.DebugWriteLine("KryoFlux plugin", "Found timestamp: {0}", blockTimestamp);
                                if(blockTimestamp < Info.CreationTime) imageInfo.CreationTime = blockTimestamp;
                                if(blockTimestamp > Info.LastModificationTime)
                                    imageInfo.LastModificationTime = blockTimestamp;
                            }

                            break;
                        }
                        case (byte)BlockIds.Flux2:
                        case (byte)BlockIds.Flux2_1:
                        case (byte)BlockIds.Flux2_2:
                        case (byte)BlockIds.Flux2_3:
                        case (byte)BlockIds.Flux2_4:
                        case (byte)BlockIds.Flux2_5:
                        case (byte)BlockIds.Flux2_6:
                        case (byte)BlockIds.Flux2_7:
                        case (byte)BlockIds.Nop2:
                            trackStream.Position++;
                            continue;
                        case (byte)BlockIds.Nop3:
                        case (byte)BlockIds.Flux3:
                            trackStream.Position += 2;
                            continue;
                        default: continue;
                    }
                }

                tracks.Add(t, trackFilter);
            }

            imageInfo.Heads     = heads;
            imageInfo.Cylinders = (uint)(tracks.Count / heads);

            throw new NotImplementedException("Flux decoding is not yet implemented.");
        }

        public byte[] ReadDiskTag(MediaTagType tag)
        {
            throw new NotImplementedException("Flux decoding is not yet implemented.");
        }

        public byte[] ReadSector(ulong sectorAddress)
        {
            return ReadSectors(sectorAddress, 1);
        }

        public byte[] ReadSectorTag(ulong sectorAddress, SectorTagType tag)
        {
            throw new NotImplementedException("Flux decoding is not yet implemented.");
        }

        public byte[] ReadSectors(ulong sectorAddress, uint length)
        {
            throw new NotImplementedException("Flux decoding is not yet implemented.");
        }

        public byte[] ReadSectorsTag(ulong sectorAddress, uint length, SectorTagType tag)
        {
            throw new NotImplementedException("Flux decoding is not yet implemented.");
        }

        public byte[] ReadSectorLong(ulong sectorAddress)
        {
            throw new NotImplementedException("Flux decoding is not yet implemented.");
        }

        public byte[] ReadSectorLong(ulong sectorAddress, uint track)
        {
            throw new FeatureUnsupportedImageException("Feature not supported by image format");
        }

        public byte[] ReadSectorsLong(ulong sectorAddress, uint length)
        {
            throw new NotImplementedException("Flux decoding is not yet implemented.");
        }

        public string Format => "KryoFlux STREAM";

        public bool? VerifySector(ulong sectorAddress)
        {
            throw new NotImplementedException("Flux decoding is not yet implemented.");
        }

        public bool? VerifySectors(ulong sectorAddress, uint length, out List<ulong> failingLbas,
                                   out                                   List<ulong> unknownLbas)
        {
            throw new NotImplementedException("Flux decoding is not yet implemented.");
        }

        public byte[] ReadSector(ulong sectorAddress, uint track)
        {
            throw new FeatureUnsupportedImageException("Feature not supported by image format");
        }

        public byte[] ReadSectorTag(ulong sectorAddress, uint track, SectorTagType tag)
        {
            throw new FeatureUnsupportedImageException("Feature not supported by image format");
        }

        public byte[] ReadSectors(ulong sectorAddress, uint length, uint track)
        {
            throw new FeatureUnsupportedImageException("Feature not supported by image format");
        }

        public byte[] ReadSectorsTag(ulong sectorAddress, uint length, uint track, SectorTagType tag)
        {
            throw new FeatureUnsupportedImageException("Feature not supported by image format");
        }

        public byte[] ReadSectorsLong(ulong sectorAddress, uint length, uint track)
        {
            throw new FeatureUnsupportedImageException("Feature not supported by image format");
        }

        public List<Partition> Partitions =>
            throw new FeatureUnsupportedImageException("Feature not supported by image format");

        public List<Track> Tracks =>
            throw new FeatureUnsupportedImageException("Feature not supported by image format");

        public List<Track> GetSessionTracks(Session session)
        {
            throw new FeatureUnsupportedImageException("Feature not supported by image format");
        }

        public List<Track> GetSessionTracks(ushort session)
        {
            throw new FeatureUnsupportedImageException("Feature not supported by image format");
        }

        public List<Session> Sessions =>
            throw new FeatureUnsupportedImageException("Feature not supported by image format");

        public bool? VerifySector(ulong sectorAddress, uint track)
        {
            throw new FeatureUnsupportedImageException("Feature not supported by image format");
        }

        public bool? VerifySectors(ulong sectorAddress, uint length, uint track, out List<ulong> failingLbas,
                                   out                                               List<ulong> unknownLbas)
        {
            throw new FeatureUnsupportedImageException("Feature not supported by image format");
        }

        public bool? VerifyMediaImage()
        {
            throw new FeatureUnsupportedImageException("Feature not supported by image format");
        }

        public List<DumpHardwareType> DumpHardware => null;
        public CICMMetadataType       CicmMetadata => null;

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        struct OobBlock
        {
            public BlockIds blockId;
            public OobTypes blockType;
            public ushort   length;
        }

        enum BlockIds : byte
        {
            Flux2   = 0x00,
            Flux2_1 = 0x01,
            Flux2_2 = 0x02,
            Flux2_3 = 0x03,
            Flux2_4 = 0x04,
            Flux2_5 = 0x05,
            Flux2_6 = 0x06,
            Flux2_7 = 0x07,
            Nop1    = 0x08,
            Nop2    = 0x09,
            Nop3    = 0x0A,
            Ovl16   = 0x0B,
            Flux3   = 0x0C,
            Oob     = 0x0D
        }

        enum OobTypes : byte
        {
            Invalid    = 0x00,
            StreamInfo = 0x01,
            Index      = 0x02,
            StreamEnd  = 0x03,
            KFInfo     = 0x04,
            EOF        = 0x0D
        }
    }
}