// /***************************************************************************
// Aaru Data Preservation Suite
// ----------------------------------------------------------------------------
//
// Filename       : Write.cs
// Author(s)      : Natalia Portillo <claunia@claunia.com>
//
// Component      : Disk image plugins.
//
// --[ Description ] ----------------------------------------------------------
//
//     Writes Aaru Format disk images.
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
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Serialization;
using Aaru.Checksums;
using Aaru.CommonTypes;
using Aaru.CommonTypes.Enums;
using Aaru.CommonTypes.Exceptions;
using Aaru.CommonTypes.Structs;
using Aaru.Console;
using Aaru.Decoders;
using CUETools.Codecs;
using CUETools.Codecs.Flake;
using Schemas;
using SharpCompress.Compressors.LZMA;
using Marshal = Aaru.Helpers.Marshal;
using TrackType = Aaru.CommonTypes.Enums.TrackType;

namespace Aaru.DiscImages
{
    public partial class AaruFormat
    {
        public bool Create(string path, MediaType mediaType, Dictionary<string, string> options, ulong sectors,
                           uint sectorSize)
        {
            uint sectorsPerBlock;
            uint dictionary;
            uint maxDdtSize;
            bool doMd5;
            bool doSha1;
            bool doSha256;
            bool doSpamsum;

            if(options != null)
            {
                if(options.TryGetValue("sectors_per_block", out string tmpValue))
                {
                    if(!uint.TryParse(tmpValue, out sectorsPerBlock))
                    {
                        ErrorMessage = "Invalid value for sectors_per_block option";

                        return false;
                    }
                }
                else
                    sectorsPerBlock = 4096;

                if(options.TryGetValue("dictionary", out tmpValue))
                {
                    if(!uint.TryParse(tmpValue, out dictionary))
                    {
                        ErrorMessage = "Invalid value for dictionary option";

                        return false;
                    }
                }
                else
                    dictionary = 1 << 25;

                if(options.TryGetValue("max_ddt_size", out tmpValue))
                {
                    if(!uint.TryParse(tmpValue, out maxDdtSize))
                    {
                        ErrorMessage = "Invalid value for max_ddt_size option";

                        return false;
                    }
                }
                else
                    maxDdtSize = 256;

                if(options.TryGetValue("md5", out tmpValue))
                {
                    if(!bool.TryParse(tmpValue, out doMd5))
                    {
                        ErrorMessage = "Invalid value for md5 option";

                        return false;
                    }
                }
                else
                    doMd5 = false;

                if(options.TryGetValue("sha1", out tmpValue))
                {
                    if(!bool.TryParse(tmpValue, out doSha1))
                    {
                        ErrorMessage = "Invalid value for sha1 option";

                        return false;
                    }
                }
                else
                    doSha1 = false;

                if(options.TryGetValue("sha256", out tmpValue))
                {
                    if(!bool.TryParse(tmpValue, out doSha256))
                    {
                        ErrorMessage = "Invalid value for sha256 option";

                        return false;
                    }
                }
                else
                    doSha256 = false;

                if(options.TryGetValue("spamsum", out tmpValue))
                {
                    if(!bool.TryParse(tmpValue, out doSpamsum))
                    {
                        ErrorMessage = "Invalid value for spamsum option";

                        return false;
                    }
                }
                else
                    doSpamsum = false;

                if(options.TryGetValue("deduplicate", out tmpValue))
                {
                    if(!bool.TryParse(tmpValue, out deduplicate))
                    {
                        ErrorMessage = "Invalid value for deduplicate option";

                        return false;
                    }
                }
                else
                    deduplicate = true;

                if(options.TryGetValue("compress", out tmpValue))
                {
                    if(!bool.TryParse(tmpValue, out compress))
                    {
                        ErrorMessage = "Invalid value for compress option";

                        return false;
                    }
                }
                else
                    compress = true;
            }
            else
            {
                sectorsPerBlock = 4096;
                dictionary      = 1 << 25;
                maxDdtSize      = 256;
                doMd5           = false;
                doSha1          = false;
                doSha256        = false;
                doSpamsum       = false;
                deduplicate     = true;
                compress        = true;
            }

            // This really, cannot happen
            if(!SupportedMediaTypes.Contains(mediaType))
            {
                ErrorMessage = $"Unsupport media format {mediaType}";

                return false;
            }

            // Calculate shift
            shift = 0;
            uint oldSectorsPerBlock = sectorsPerBlock;

            while(sectorsPerBlock > 1)
            {
                sectorsPerBlock >>= 1;
                shift++;
            }

            AaruConsole.DebugWriteLine("Aaru Format plugin", "Got a shift of {0} for {1} sectors per block", shift,
                                       oldSectorsPerBlock);

            imageInfo = new ImageInfo
            {
                MediaType    = mediaType, SectorSize = sectorSize, Sectors = sectors,
                XmlMediaType = GetXmlMediaType(mediaType)
            };

            try
            {
                imageStream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch(IOException e)
            {
                ErrorMessage = $"Could not create new image file, exception {e.Message}";

                return false;
            }

            // Check if appending to an existing image
            if(imageStream.Length > Marshal.SizeOf<AaruHeader>())
            {
                structureBytes = new byte[Marshal.SizeOf<AaruHeader>()];
                imageStream.Read(structureBytes, 0, structureBytes.Length);
                header = Marshal.ByteArrayToStructureLittleEndian<AaruHeader>(structureBytes);

                if(header.identifier != DIC_MAGIC &&
                   header.identifier != AARU_MAGIC)
                {
                    ErrorMessage = "Cannot append to a non Aaru Format image";

                    return false;
                }

                if(header.imageMajorVersion > AARUFMT_VERSION)
                {
                    ErrorMessage = $"Cannot append to an unknown image version {header.imageMajorVersion}";

                    return false;
                }

                if(header.mediaType != mediaType)
                {
                    ErrorMessage =
                        $"Cannot write a media with type {mediaType} to an image with type {header.mediaType}";

                    return false;
                }
            }
            else
            {
                header = new AaruHeader
                {
                    identifier = AARU_MAGIC, mediaType = mediaType, creationTime = DateTime.UtcNow.ToFileTimeUtc()
                };

                imageStream.Write(new byte[Marshal.SizeOf<AaruHeader>()], 0, Marshal.SizeOf<AaruHeader>());
            }

            header.application             = "Aaru";
            header.imageMajorVersion       = AARUFMT_VERSION;
            header.imageMinorVersion       = 0;
            header.applicationMajorVersion = (byte)typeof(AaruFormat).Assembly.GetName().Version.Major;
            header.applicationMinorVersion = (byte)typeof(AaruFormat).Assembly.GetName().Version.Minor;

            index = new List<IndexEntry>();

            // If there exists an index, we are appending, so read index
            if(header.indexOffset > 0)
            {
                // Can't calculate checksum of an appended image
                md5Provider     = null;
                sha1Provider    = null;
                sha256Provider  = null;
                spamsumProvider = null;

                imageStream.Position = (long)header.indexOffset;
                structureBytes       = new byte[Marshal.SizeOf<IndexHeader>()];
                imageStream.Read(structureBytes, 0, structureBytes.Length);
                IndexHeader idxHeader = Marshal.SpanToStructureLittleEndian<IndexHeader>(structureBytes);

                if(idxHeader.identifier != BlockType.Index)
                {
                    ErrorMessage = "Index not found in existing image, cannot continue";

                    return false;
                }

                AaruConsole.DebugWriteLine("Aaru Format plugin", "Index at {0} contains {1} entries",
                                           header.indexOffset, idxHeader.entries);

                for(ushort i = 0; i < idxHeader.entries; i++)
                {
                    structureBytes = new byte[Marshal.SizeOf<IndexEntry>()];
                    imageStream.Read(structureBytes, 0, structureBytes.Length);
                    IndexEntry entry = Marshal.SpanToStructureLittleEndian<IndexEntry>(structureBytes);

                    AaruConsole.DebugWriteLine("Aaru Format plugin",
                                               "Block type {0} with data type {1} is indexed to be at {2}",
                                               entry.blockType, entry.dataType, entry.offset);

                    index.Add(entry);
                }

                // Invalidate previous checksum block
                index.RemoveAll(t => t.blockType == BlockType.ChecksumBlock && t.dataType == DataType.NoData);

                bool foundUserDataDdt = false;

                foreach(IndexEntry entry in index)
                {
                    imageStream.Position = (long)entry.offset;

                    switch(entry.blockType)
                    {
                        case BlockType.DataBlock:
                            switch(entry.dataType)
                            {
                                case DataType.CdSectorPrefix:
                                case DataType.CdSectorSuffix:
                                case DataType.CdSectorPrefixCorrected:
                                case DataType.CdSectorSuffixCorrected:
                                case DataType.CdSectorSubchannel:
                                case DataType.AppleProfileTag:
                                case DataType.AppleSonyTag:
                                case DataType.PriamDataTowerTag: break;
                                default: continue;
                            }

                            structureBytes = new byte[Marshal.SizeOf<BlockHeader>()];
                            imageStream.Read(structureBytes, 0, structureBytes.Length);
                            BlockHeader blockHeader = Marshal.SpanToStructureLittleEndian<BlockHeader>(structureBytes);
                            imageInfo.ImageSize += blockHeader.cmpLength;

                            if(blockHeader.identifier != entry.blockType)
                            {
                                AaruConsole.DebugWriteLine("Aaru Format plugin",
                                                           "Incorrect identifier for data block at position {0}",
                                                           entry.offset);

                                break;
                            }

                            if(blockHeader.type != entry.dataType)
                            {
                                AaruConsole.DebugWriteLine("Aaru Format plugin",
                                                           "Expected block with data type {0} at position {1} but found data type {2}",
                                                           entry.dataType, entry.offset, blockHeader.type);

                                break;
                            }

                            byte[] data;

                            AaruConsole.DebugWriteLine("Aaru Format plugin",
                                                       "Found data block type {0} at position {1}", entry.dataType,
                                                       entry.offset);

                            if(blockHeader.compression == CompressionType.Lzma)
                            {
                                byte[] compressedTag  = new byte[blockHeader.cmpLength - LZMA_PROPERTIES_LENGTH];
                                byte[] lzmaProperties = new byte[LZMA_PROPERTIES_LENGTH];
                                imageStream.Read(lzmaProperties, 0, LZMA_PROPERTIES_LENGTH);
                                imageStream.Read(compressedTag, 0, compressedTag.Length);
                                var compressedTagMs = new MemoryStream(compressedTag);
                                var lzmaBlock       = new LzmaStream(lzmaProperties, compressedTagMs);
                                data = new byte[blockHeader.length];
                                lzmaBlock.Read(data, 0, (int)blockHeader.length);
                                lzmaBlock.Close();
                                compressedTagMs.Close();
                            }
                            else if(blockHeader.compression == CompressionType.None)
                            {
                                data = new byte[blockHeader.length];
                                imageStream.Read(data, 0, (int)blockHeader.length);
                            }
                            else
                            {
                                AaruConsole.DebugWriteLine("Aaru Format plugin",
                                                           "Found unknown compression type {0}, continuing...",
                                                           (ushort)blockHeader.compression);

                                break;
                            }

                            Crc64Context.Data(data, out byte[] blockCrc);
                            blockCrc = blockCrc.ToArray();

                            if(BitConverter.ToUInt64(blockCrc, 0) != blockHeader.crc64)
                            {
                                AaruConsole.DebugWriteLine("Aaru Format plugin",
                                                           "Incorrect CRC found: 0x{0:X16} found, expected 0x{1:X16}, continuing...",
                                                           BitConverter.ToUInt64(blockCrc, 0), blockHeader.crc64);

                                break;
                            }

                            switch(entry.dataType)
                            {
                                case DataType.CdSectorPrefix:
                                    sectorPrefix = data;

                                    break;
                                case DataType.CdSectorSuffix:
                                    sectorSuffix = data;

                                    break;
                                case DataType.CdSectorPrefixCorrected:
                                    sectorPrefixMs = new MemoryStream();
                                    sectorPrefixMs.Write(data, 0, data.Length);

                                    break;
                                case DataType.CdSectorSuffixCorrected:
                                    sectorSuffixMs = new MemoryStream();
                                    sectorSuffixMs.Write(data, 0, data.Length);

                                    break;
                                case DataType.CdSectorSubchannel:
                                case DataType.AppleProfileTag:
                                case DataType.AppleSonyTag:
                                case DataType.PriamDataTowerTag:
                                    sectorSubchannel = data;

                                    break;
                            }

                            break;
                        case BlockType.DeDuplicationTable:
                            // Only user data deduplication tables are used right now
                            if(entry.dataType == DataType.UserData)
                            {
                                structureBytes = new byte[Marshal.SizeOf<DdtHeader>()];
                                imageStream.Read(structureBytes, 0, structureBytes.Length);

                                DdtHeader ddtHeader =
                                    Marshal.ByteArrayToStructureLittleEndian<DdtHeader>(structureBytes);

                                if(ddtHeader.identifier != BlockType.DeDuplicationTable)
                                    break;

                                if(ddtHeader.entries != imageInfo.Sectors &&
                                   !IsTape)
                                {
                                    ErrorMessage =
                                        $"Trying to write a media with {imageInfo.Sectors} sectors to an image with {ddtHeader.entries} sectors, not continuing...";

                                    return false;
                                }

                                shift = ddtHeader.shift;

                                switch(ddtHeader.compression)
                                {
                                    case CompressionType.Lzma:
                                        AaruConsole.DebugWriteLine("Aaru Format plugin", "Decompressing DDT...");

                                        DateTime ddtStart = DateTime.UtcNow;

                                        byte[] compressedDdt = new byte[ddtHeader.cmpLength - LZMA_PROPERTIES_LENGTH];

                                        byte[] lzmaProperties = new byte[LZMA_PROPERTIES_LENGTH];
                                        imageStream.Read(lzmaProperties, 0, LZMA_PROPERTIES_LENGTH);
                                        imageStream.Read(compressedDdt, 0, compressedDdt.Length);
                                        var    compressedDdtMs = new MemoryStream(compressedDdt);
                                        var    lzmaDdt         = new LzmaStream(lzmaProperties, compressedDdtMs);
                                        byte[] decompressedDdt = new byte[ddtHeader.length];
                                        lzmaDdt.Read(decompressedDdt, 0, (int)ddtHeader.length);
                                        lzmaDdt.Close();
                                        compressedDdtMs.Close();
                                        userDataDdt = MemoryMarshal.Cast<byte, ulong>(decompressedDdt).ToArray();
                                        DateTime ddtEnd = DateTime.UtcNow;
                                        inMemoryDdt = true;

                                        AaruConsole.DebugWriteLine("Aaru Format plugin",
                                                                   "Took {0} seconds to decompress DDT",
                                                                   (ddtEnd - ddtStart).TotalSeconds);

                                        break;
                                    case CompressionType.None:
                                        inMemoryDdt          = false;
                                        outMemoryDdtPosition = (long)entry.offset;

                                        break;
                                    default:
                                        throw new
                                            ImageNotSupportedException($"Found unsupported compression algorithm {(ushort)ddtHeader.compression}");
                                }

                                if(IsTape)
                                {
                                    tapeDdt = new Dictionary<ulong, ulong>();

                                    for(long i = 0; i < userDataDdt.LongLength; i++)
                                        tapeDdt.Add((ulong)i, userDataDdt[i]);

                                    userDataDdt = null;
                                }

                                foundUserDataDdt = true;
                            }
                            else if(entry.dataType == DataType.CdSectorPrefixCorrected ||
                                    entry.dataType == DataType.CdSectorSuffixCorrected)
                            {
                                structureBytes = new byte[Marshal.SizeOf<DdtHeader>()];
                                imageStream.Read(structureBytes, 0, structureBytes.Length);

                                DdtHeader ddtHeader =
                                    Marshal.ByteArrayToStructureLittleEndian<DdtHeader>(structureBytes);

                                if(ddtHeader.identifier != BlockType.DeDuplicationTable)
                                    break;

                                if(ddtHeader.entries != imageInfo.Sectors)
                                {
                                    ErrorMessage =
                                        $"Trying to write a media with {imageInfo.Sectors} sectors to an image with {ddtHeader.entries} sectors, not continuing...";

                                    return false;
                                }

                                byte[] decompressedDdt = new byte[ddtHeader.length];

                                switch(ddtHeader.compression)
                                {
                                    case CompressionType.Lzma:
                                        AaruConsole.DebugWriteLine("Aaru Format plugin", "Decompressing DDT...");

                                        DateTime ddtStart = DateTime.UtcNow;

                                        byte[] compressedDdt = new byte[ddtHeader.cmpLength - LZMA_PROPERTIES_LENGTH];

                                        byte[] lzmaProperties = new byte[LZMA_PROPERTIES_LENGTH];
                                        imageStream.Read(lzmaProperties, 0, LZMA_PROPERTIES_LENGTH);
                                        imageStream.Read(compressedDdt, 0, compressedDdt.Length);
                                        var compressedDdtMs = new MemoryStream(compressedDdt);
                                        var lzmaDdt         = new LzmaStream(lzmaProperties, compressedDdtMs);
                                        lzmaDdt.Read(decompressedDdt, 0, (int)ddtHeader.length);
                                        lzmaDdt.Close();
                                        compressedDdtMs.Close();
                                        DateTime ddtEnd = DateTime.UtcNow;

                                        AaruConsole.DebugWriteLine("Aaru Format plugin",
                                                                   "Took {0} seconds to decompress DDT",
                                                                   (ddtEnd - ddtStart).TotalSeconds);

                                        break;
                                    case CompressionType.None:
                                        imageStream.Read(decompressedDdt, 0, decompressedDdt.Length);

                                        break;
                                    default:
                                        throw new
                                            ImageNotSupportedException($"Found unsupported compression algorithm {(ushort)ddtHeader.compression}");
                                }

                                uint[] cdDdt = MemoryMarshal.Cast<byte, uint>(decompressedDdt).ToArray();

                                switch(entry.dataType)
                                {
                                    case DataType.CdSectorPrefixCorrected:
                                        sectorPrefixDdt = cdDdt;

                                        break;
                                    case DataType.CdSectorSuffixCorrected:
                                        sectorSuffixDdt = cdDdt;

                                        break;
                                }
                            }

                            break;

                        // CICM XML metadata block
                        case BlockType.CicmBlock:
                            structureBytes = new byte[Marshal.SizeOf<CicmMetadataBlock>()];
                            imageStream.Read(structureBytes, 0, structureBytes.Length);

                            CicmMetadataBlock cicmBlock =
                                Marshal.SpanToStructureLittleEndian<CicmMetadataBlock>(structureBytes);

                            if(cicmBlock.identifier != BlockType.CicmBlock)
                                break;

                            AaruConsole.DebugWriteLine("Aaru Format plugin",
                                                       "Found CICM XML metadata block at position {0}", entry.offset);

                            byte[] cicmBytes = new byte[cicmBlock.length];
                            imageStream.Read(cicmBytes, 0, cicmBytes.Length);
                            var cicmMs = new MemoryStream(cicmBytes);
                            var cicmXs = new XmlSerializer(typeof(CICMMetadataType));

                            try
                            {
                                var sr = new StreamReader(cicmMs);
                                CicmMetadata = (CICMMetadataType)cicmXs.Deserialize(sr);
                                sr.Close();
                            }
                            catch(XmlException ex)
                            {
                                AaruConsole.DebugWriteLine("Aaru Format plugin",
                                                           "Exception {0} processing CICM XML metadata block",
                                                           ex.Message);

                                CicmMetadata = null;
                            }

                            break;

                        // Dump hardware block
                        case BlockType.DumpHardwareBlock:
                            structureBytes = new byte[Marshal.SizeOf<DumpHardwareHeader>()];
                            imageStream.Read(structureBytes, 0, structureBytes.Length);

                            DumpHardwareHeader dumpBlock =
                                Marshal.SpanToStructureLittleEndian<DumpHardwareHeader>(structureBytes);

                            if(dumpBlock.identifier != BlockType.DumpHardwareBlock)
                                break;

                            AaruConsole.DebugWriteLine("Aaru Format plugin",
                                                       "Found dump hardware block at position {0}", entry.offset);

                            structureBytes = new byte[dumpBlock.length];
                            imageStream.Read(structureBytes, 0, structureBytes.Length);
                            Crc64Context.Data(structureBytes, out byte[] dumpCrc);

                            if(BitConverter.ToUInt64(dumpCrc, 0) != dumpBlock.crc64)
                            {
                                AaruConsole.DebugWriteLine("Aaru Format plugin",
                                                           "Incorrect CRC found: 0x{0:X16} found, expected 0x{1:X16}, continuing...",
                                                           BitConverter.ToUInt64(dumpCrc, 0), dumpBlock.crc64);

                                break;
                            }

                            imageStream.Position -= structureBytes.Length;

                            DumpHardware = new List<DumpHardwareType>();

                            for(ushort i = 0; i < dumpBlock.entries; i++)
                            {
                                structureBytes = new byte[Marshal.SizeOf<DumpHardwareEntry>()];
                                imageStream.Read(structureBytes, 0, structureBytes.Length);

                                DumpHardwareEntry dumpEntry =
                                    Marshal.SpanToStructureLittleEndian<DumpHardwareEntry>(structureBytes);

                                var dump = new DumpHardwareType
                                {
                                    Software = new SoftwareType(), Extents = new ExtentType[dumpEntry.extents]
                                };

                                byte[] tmp;

                                if(dumpEntry.manufacturerLength > 0)
                                {
                                    tmp = new byte[dumpEntry.manufacturerLength - 1];
                                    imageStream.Read(tmp, 0, tmp.Length);
                                    imageStream.Position += 1;
                                    dump.Manufacturer    =  Encoding.UTF8.GetString(tmp);
                                }

                                if(dumpEntry.modelLength > 0)
                                {
                                    tmp = new byte[dumpEntry.modelLength - 1];
                                    imageStream.Read(tmp, 0, tmp.Length);
                                    imageStream.Position += 1;
                                    dump.Model           =  Encoding.UTF8.GetString(tmp);
                                }

                                if(dumpEntry.revisionLength > 0)
                                {
                                    tmp = new byte[dumpEntry.revisionLength - 1];
                                    imageStream.Read(tmp, 0, tmp.Length);
                                    imageStream.Position += 1;
                                    dump.Revision        =  Encoding.UTF8.GetString(tmp);
                                }

                                if(dumpEntry.firmwareLength > 0)
                                {
                                    tmp = new byte[dumpEntry.firmwareLength - 1];
                                    imageStream.Read(tmp, 0, tmp.Length);
                                    imageStream.Position += 1;
                                    dump.Firmware        =  Encoding.UTF8.GetString(tmp);
                                }

                                if(dumpEntry.serialLength > 0)
                                {
                                    tmp = new byte[dumpEntry.serialLength - 1];
                                    imageStream.Read(tmp, 0, tmp.Length);
                                    imageStream.Position += 1;
                                    dump.Serial          =  Encoding.UTF8.GetString(tmp);
                                }

                                if(dumpEntry.softwareNameLength > 0)
                                {
                                    tmp = new byte[dumpEntry.softwareNameLength - 1];
                                    imageStream.Read(tmp, 0, tmp.Length);
                                    imageStream.Position += 1;
                                    dump.Software.Name   =  Encoding.UTF8.GetString(tmp);
                                }

                                if(dumpEntry.softwareVersionLength > 0)
                                {
                                    tmp = new byte[dumpEntry.softwareVersionLength - 1];
                                    imageStream.Read(tmp, 0, tmp.Length);
                                    imageStream.Position  += 1;
                                    dump.Software.Version =  Encoding.UTF8.GetString(tmp);
                                }

                                if(dumpEntry.softwareOperatingSystemLength > 0)
                                {
                                    tmp                  =  new byte[dumpEntry.softwareOperatingSystemLength - 1];
                                    imageStream.Position += 1;
                                    imageStream.Read(tmp, 0, tmp.Length);
                                    dump.Software.OperatingSystem = Encoding.UTF8.GetString(tmp);
                                }

                                tmp = new byte[16];

                                for(uint j = 0; j < dumpEntry.extents; j++)
                                {
                                    imageStream.Read(tmp, 0, tmp.Length);

                                    dump.Extents[j] = new ExtentType
                                    {
                                        Start = BitConverter.ToUInt64(tmp, 0), End = BitConverter.ToUInt64(tmp, 8)
                                    };
                                }

                                dump.Extents = dump.Extents.OrderBy(t => t.Start).ToArray();

                                if(dump.Extents.Length > 0)
                                    DumpHardware.Add(dump);
                            }

                            if(DumpHardware.Count == 0)
                                DumpHardware = null;

                            break;

                        // Tape partition block
                        case BlockType.TapePartitionBlock:
                            structureBytes = new byte[Marshal.SizeOf<TapePartitionHeader>()];
                            imageStream.Read(structureBytes, 0, structureBytes.Length);

                            TapePartitionHeader partitionHeader =
                                Marshal.SpanToStructureLittleEndian<TapePartitionHeader>(structureBytes);

                            if(partitionHeader.identifier != BlockType.TapePartitionBlock)
                                break;

                            AaruConsole.DebugWriteLine("Aaru Format plugin",
                                                       "Found tape partition block at position {0}", entry.offset);

                            byte[] tapePartitionBytes = new byte[partitionHeader.length];
                            imageStream.Read(tapePartitionBytes, 0, tapePartitionBytes.Length);

                            Span<TapePartitionEntry> tapePartitions =
                                MemoryMarshal.Cast<byte, TapePartitionEntry>(tapePartitionBytes);

                            TapePartitions = new List<TapePartition>();

                            foreach(TapePartitionEntry tapePartition in tapePartitions)
                                TapePartitions.Add(new TapePartition
                                {
                                    FirstBlock = tapePartition.FirstBlock, LastBlock = tapePartition.LastBlock,
                                    Number     = tapePartition.Number
                                });

                            IsTape = true;

                            break;

                        // Tape file block
                        case BlockType.TapeFileBlock:
                            structureBytes = new byte[Marshal.SizeOf<TapeFileHeader>()];
                            imageStream.Read(structureBytes, 0, structureBytes.Length);

                            TapeFileHeader fileHeader =
                                Marshal.SpanToStructureLittleEndian<TapeFileHeader>(structureBytes);

                            if(fileHeader.identifier != BlockType.TapeFileBlock)
                                break;

                            AaruConsole.DebugWriteLine("Aaru Format plugin", "Found tape file block at position {0}",
                                                       entry.offset);

                            byte[] tapeFileBytes = new byte[fileHeader.length];
                            imageStream.Read(tapeFileBytes, 0, tapeFileBytes.Length);
                            Span<TapeFileEntry> tapeFiles = MemoryMarshal.Cast<byte, TapeFileEntry>(tapeFileBytes);
                            Files = new List<TapeFile>();

                            foreach(TapeFileEntry file in tapeFiles)
                                Files.Add(new TapeFile
                                {
                                    FirstBlock = file.FirstBlock, LastBlock = file.LastBlock,
                                    Partition  = file.Partition, File       = file.File
                                });

                            IsTape = true;

                            break;
                    }
                }

                if(!foundUserDataDdt)
                {
                    ErrorMessage = "Could not find user data deduplication table.";

                    return false;
                }

                if(sectorSuffixMs  == null ||
                   sectorSuffixDdt == null ||
                   sectorPrefixMs  == null ||
                   sectorPrefixDdt == null)
                {
                    sectorSuffixMs  = null;
                    sectorSuffixDdt = null;
                    sectorPrefixMs  = null;
                    sectorPrefixDdt = null;
                }
            }

            // Creating new
            else
            {
                // Checking that DDT is smaller than requested size
                inMemoryDdt = sectors <= (maxDdtSize * 1024 * 1024) / sizeof(ulong);

                // If in memory, easy
                if(inMemoryDdt)
                {
                    if(IsTape)
                        tapeDdt = new Dictionary<ulong, ulong>();
                    else
                        userDataDdt = new ulong[sectors];
                }

                // If not, create the block, add to index, and enlarge the file to allow the DDT to exist on-disk
                else
                {
                    outMemoryDdtPosition = imageStream.Position;

                    index.Add(new IndexEntry
                    {
                        blockType = BlockType.DeDuplicationTable, dataType = DataType.UserData,
                        offset    = (ulong)outMemoryDdtPosition
                    });

                    // CRC64 will be calculated later
                    var ddtHeader = new DdtHeader
                    {
                        identifier  = BlockType.DeDuplicationTable, type = DataType.UserData,
                        compression = CompressionType.None, shift        = shift, entries = sectors,
                        cmpLength   = sectors * sizeof(ulong), length    = sectors * sizeof(ulong)
                    };

                    structureBytes = new byte[Marshal.SizeOf<DdtHeader>()];
                    MemoryMarshal.Write(structureBytes, ref ddtHeader);
                    imageStream.Write(structureBytes, 0, structureBytes.Length);
                    structureBytes = null;

                    imageStream.Position += (long)(sectors * sizeof(ulong)) - 1;
                    imageStream.WriteByte(0);
                }

                if(doMd5)
                    md5Provider = new Md5Context();

                if(doSha1)
                    sha1Provider = new Sha1Context();

                if(doSha256)
                    sha256Provider = new Sha256Context();

                if(doSpamsum)
                    spamsumProvider = new SpamSumContext();
            }

            AaruConsole.DebugWriteLine("Aaru Format plugin", "In memory DDT?: {0}", inMemoryDdt);

            // Initialize tables
            imageStream.Seek(0, SeekOrigin.End);
            mediaTags          = new Dictionary<MediaTagType, byte[]>();
            checksumProvider   = SHA256.Create();
            deduplicationTable = new Dictionary<string, ulong>();
            trackIsrcs         = new Dictionary<byte, string>();
            trackFlags         = new Dictionary<byte, byte>();

            // Initialize compressors properties (all maxed)
            lzmaEncoderProperties = new LzmaEncoderProperties(true, (int)dictionary, 273);

            flakeWriterSettings = new EncoderSettings()
            {
                PCM                = AudioPCMConfig.RedBook, DoMD5 = false,
                BlockSize          = (1 << shift) * SAMPLES_PER_SECTOR,
                MinFixedOrder      = 0, MaxFixedOrder = 4,
                MinLPCOrder        = 1, MaxLPCOrder   = 32,
                MaxPartitionOrder  = 8, StereoMethod  = StereoMethod.Evaluate,
                PredictionType     = PredictionType.Search,
                WindowMethod       = WindowMethod.EvaluateN, EstimationDepth = 5,
                MinPrecisionSearch = 1,
                MaxPrecisionSearch = 1, TukeyParts = 0,
                TukeyOverlap       = 1.0, TukeyP   = 1.0,
                AllowNonSubset     = true
            };

            // Check if FLAKE's block size is bigger than what we want
            if(flakeWriterSettings.BlockSize > MAX_FLAKE_BLOCK)
                flakeWriterSettings.BlockSize = MAX_FLAKE_BLOCK;

            if(flakeWriterSettings.BlockSize < MIN_FLAKE_BLOCK)
                flakeWriterSettings.BlockSize = MIN_FLAKE_BLOCK;

            AudioEncoder.Vendor = "Aaru";

            IsWriting    = true;
            ErrorMessage = null;

            return true;
        }

        public bool WriteMediaTag(byte[] data, MediaTagType tag)
        {
            if(!IsWriting)
            {
                ErrorMessage = "Tried to write on a non-writable image";

                return false;
            }

            if(mediaTags.ContainsKey(tag))
                mediaTags.Remove(tag);

            mediaTags.Add(tag, data);

            ErrorMessage = "";

            return true;
        }

        public bool WriteSector(byte[] data, ulong sectorAddress)
        {
            if(!IsWriting)
            {
                ErrorMessage = "Tried to write on a non-writable image";

                return false;
            }

            if(sectorAddress >= Info.Sectors &&
               !IsTape)
            {
                ErrorMessage = "Tried to write past image size";

                return false;
            }

            if((imageInfo.XmlMediaType != XmlMediaType.OpticalDisc || !writingLong) &&
               !rewinded)
            {
                if(sectorAddress <= lastWrittenBlock && alreadyWrittenZero)
                {
                    rewinded        = true;
                    md5Provider     = null;
                    sha1Provider    = null;
                    sha256Provider  = null;
                    spamsumProvider = null;
                }

                md5Provider?.Update(data);
                sha1Provider?.Update(data);
                sha256Provider?.Update(data);
                spamsumProvider?.Update(data);
                lastWrittenBlock = sectorAddress;
            }

            if(sectorAddress == 0)
                alreadyWrittenZero = true;

            byte[] hash = null;
            writtenSectors++;

            // Compute hash only if asked to deduplicate, or the sector is empty (those will always be deduplicated)
            if(deduplicate || ArrayHelpers.ArrayIsNullOrEmpty(data))
                hash = checksumProvider.ComputeHash(data);

            string hashString = null;

            if(hash != null)
            {
                var hashSb = new StringBuilder();

                foreach(byte h in hash)
                    hashSb.Append(h.ToString("x2"));

                hashString = hashSb.ToString();

                if(deduplicationTable.TryGetValue(hashString, out ulong pointer))
                {
                    SetDdtEntry(sectorAddress, pointer);
                    ErrorMessage = "";

                    return true;
                }
            }

            var trk = new Track();

            // If optical disc check track
            if(imageInfo.XmlMediaType == XmlMediaType.OpticalDisc)
            {
                trk = Tracks.FirstOrDefault(t => sectorAddress >= t.TrackStartSector &&
                                                 sectorAddress <= t.TrackEndSector);

                if(trk.TrackSequence    == 0 &&
                   trk.TrackStartSector == 0 &&
                   trk.TrackEndSector   == 0)
                    throw new ArgumentOutOfRangeException(nameof(sectorAddress),
                                                          "Can't found track containing requested sector");
            }

            // Close current block first
            if(blockStream != null &&

               // When sector siz changes
               (currentBlockHeader.sectorSize != data.Length ||

                // When block if filled
                currentBlockOffset == 1 << shift ||

                // When we change to/from CompactDisc audio
                (currentBlockHeader.compression == CompressionType.Flac && trk.TrackType != TrackType.Audio)))
            {
                currentBlockHeader.length = currentBlockOffset * currentBlockHeader.sectorSize;
                currentBlockHeader.crc64  = BitConverter.ToUInt64(crc64.Final(), 0);

                var cmpCrc64Context = new Crc64Context();

                byte[] lzmaProperties = new byte[0];

                if(currentBlockHeader.compression == CompressionType.Flac)
                {
                    long remaining = (currentBlockOffset * SAMPLES_PER_SECTOR) % flakeWriter.Settings.BlockSize;

                    // Fill FLAC block
                    if(remaining != 0)
                    {
                        var audioBuffer =
                            new AudioBuffer(AudioPCMConfig.RedBook, new byte[remaining * 4], (int)remaining);

                        flakeWriter.Write(audioBuffer);
                    }

                    // This trick because CUETools.Codecs.Flake closes the underlying stream
                    long   realLength = blockStream.Length;
                    byte[] buffer     = new byte[realLength];
                    flakeWriter.Close();
                    Array.Copy(blockStream.GetBuffer(), 0, buffer, 0, realLength);
                    blockStream = new MemoryStream(buffer);
                }
                else if(currentBlockHeader.compression == CompressionType.Lzma)
                {
                    lzmaProperties = lzmaBlockStream.Properties;
                    lzmaBlockStream.Close();
                    lzmaBlockStream = null;
                    cmpCrc64Context.Update(lzmaProperties);

                    if(blockStream.Length > decompressedStream.Length)
                        currentBlockHeader.compression = CompressionType.None;
                }

                if(currentBlockHeader.compression == CompressionType.None)
                {
                    blockStream                 = decompressedStream;
                    currentBlockHeader.cmpCrc64 = currentBlockHeader.crc64;
                }
                else
                {
                    cmpCrc64Context.Update(blockStream.ToArray());
                    currentBlockHeader.cmpCrc64 = BitConverter.ToUInt64(cmpCrc64Context.Final(), 0);
                }

                currentBlockHeader.cmpLength = (uint)blockStream.Length;

                if(currentBlockHeader.compression == CompressionType.Lzma)
                    currentBlockHeader.cmpLength += LZMA_PROPERTIES_LENGTH;

                index.Add(new IndexEntry
                {
                    blockType = BlockType.DataBlock, dataType = DataType.UserData, offset = (ulong)imageStream.Position
                });

                structureBytes = new byte[Marshal.SizeOf<BlockHeader>()];
                MemoryMarshal.Write(structureBytes, ref currentBlockHeader);
                imageStream.Write(structureBytes, 0, structureBytes.Length);
                structureBytes = null;

                if(currentBlockHeader.compression == CompressionType.Lzma)
                    imageStream.Write(lzmaProperties, 0, lzmaProperties.Length);

                imageStream.Write(blockStream.ToArray(), 0, (int)blockStream.Length);
                blockStream.Close();
                blockStream = null;
                GC.Collect(GC.MaxGeneration, GCCollectionMode.Optimized, false);
                currentBlockOffset = 0;
            }

            // No block set
            if(blockStream == null)
            {
                currentBlockHeader = new BlockHeader
                {
                    identifier  = BlockType.DataBlock,
                    type        = DataType.UserData,
                    compression = compress ? CompressionType.Lzma : CompressionType.None, sectorSize = (uint)data.Length
                };

                if(imageInfo.XmlMediaType == XmlMediaType.OpticalDisc &&
                   trk.TrackType          == TrackType.Audio          &&
                   compress)
                    currentBlockHeader.compression = CompressionType.Flac;

                // JaguarCD stores data in audio tracks. FLAC is too inefficient, use LZMA there.
                // VideoNow stores video in audio tracks, and LZMA works better too.
                if(((imageInfo.MediaType == MediaType.JaguarCD && trk.TrackSession > 1) ||
                    imageInfo.MediaType == MediaType.VideoNow                           ||
                    imageInfo.MediaType == MediaType.VideoNowColor                      ||
                    imageInfo.MediaType == MediaType.VideoNowXp) &&
                   trk.TrackType == TrackType.Audio              &&
                   compress                                      &&
                   currentBlockHeader.compression == CompressionType.Flac)
                    currentBlockHeader.compression = CompressionType.Lzma;

                blockStream        = new MemoryStream();
                decompressedStream = new MemoryStream();

                switch(currentBlockHeader.compression)
                {
                    case CompressionType.Flac:
                        flakeWriter = new AudioEncoder(flakeWriterSettings, "", blockStream)
                        {
                            DoSeekTable = false
                        };

                        break;
                    case CompressionType.Lzma:
                        lzmaBlockStream = new LzmaStream(lzmaEncoderProperties, false, blockStream);

                        break;
                    default:
                        lzmaBlockStream = null;

                        break;
                }

                crc64 = new Crc64Context();
            }

            ulong ddtEntry = (ulong)((imageStream.Position << shift) + currentBlockOffset);

            if(hash != null)
                deduplicationTable.Add(hashString, ddtEntry);

            if(currentBlockHeader.compression == CompressionType.Flac)
            {
                var audioBuffer = new AudioBuffer(AudioPCMConfig.RedBook, data, SAMPLES_PER_SECTOR);
                flakeWriter.Write(audioBuffer);
            }
            else
            {
                decompressedStream.Write(data, 0, data.Length);

                if(currentBlockHeader.compression == CompressionType.Lzma)
                    lzmaBlockStream.Write(data, 0, data.Length);
            }

            SetDdtEntry(sectorAddress, ddtEntry);
            crc64.Update(data);
            currentBlockOffset++;

            ErrorMessage = "";

            return true;
        }

        public bool WriteSectors(byte[] data, ulong sectorAddress, uint length)
        {
            if(!IsWriting)
            {
                ErrorMessage = "Tried to write on a non-writable image";

                return false;
            }

            if(sectorAddress + length > Info.Sectors)
            {
                ErrorMessage = "Tried to write past image size";

                return false;
            }

            uint sectorSize = (uint)(data.Length / length);

            for(uint i = 0; i < length; i++)
            {
                byte[] tmp = new byte[sectorSize];
                Array.Copy(data, i * sectorSize, tmp, 0, sectorSize);

                if(!WriteSector(tmp, sectorAddress + i))
                    return false;
            }

            ErrorMessage = "";

            return true;
        }

        public bool WriteSectorLong(byte[] data, ulong sectorAddress)
        {
            if(!IsWriting)
            {
                ErrorMessage = "Tried to write on a non-writable image";

                return false;
            }

            byte[] sector;

            switch(imageInfo.XmlMediaType)
            {
                case XmlMediaType.OpticalDisc:
                    Track track =
                        Tracks.FirstOrDefault(trk => sectorAddress >= trk.TrackStartSector &&
                                                     sectorAddress <= trk.TrackEndSector);

                    if(track.TrackSequence    == 0 &&
                       track.TrackStartSector == 0 &&
                       track.TrackEndSector   == 0)
                    {
                        ErrorMessage = $"Can't found track containing {sectorAddress}";

                        return false;
                    }

                    if(data.Length != 2352)
                    {
                        ErrorMessage = "Incorrect data size";

                        return false;
                    }

                    writingLong = true;

                    if(!rewinded)
                    {
                        if(sectorAddress <= lastWrittenBlock && alreadyWrittenZero)
                        {
                            rewinded        = true;
                            md5Provider     = null;
                            sha1Provider    = null;
                            sha256Provider  = null;
                            spamsumProvider = null;
                        }

                        md5Provider?.Update(data);
                        sha1Provider?.Update(data);
                        sha256Provider?.Update(data);
                        spamsumProvider?.Update(data);
                        lastWrittenBlock = sectorAddress;
                    }

                    bool prefixCorrect;
                    int  minute;
                    int  second;
                    int  frame;
                    int  storedLba;

                    // Split raw cd sector data in prefix (sync, header), user data and suffix (edc, ecc p, ecc q)
                    switch(track.TrackType)
                    {
                        case TrackType.Audio:
                        case TrackType.Data: return WriteSector(data, sectorAddress);
                        case TrackType.CdMode1:
                            if(sectorPrefix != null &&
                               sectorSuffix != null)
                            {
                                sector = new byte[2048];
                                Array.Copy(data, 0, sectorPrefix, (int)sectorAddress * 16, 16);
                                Array.Copy(data, 16, sector, 0, 2048);
                                Array.Copy(data, 2064, sectorSuffix, (int)sectorAddress * 288, 288);

                                return WriteSector(sector, sectorAddress);
                            }

                            if(sectorSuffixMs == null)
                                sectorSuffixMs = new MemoryStream();

                            if(sectorPrefixMs == null)
                                sectorPrefixMs = new MemoryStream();

                            if(sectorSuffixDdt == null)
                            {
                                sectorSuffixDdt = new uint[imageInfo.Sectors];
                                EccInit();
                            }

                            if(sectorPrefixDdt == null)
                                sectorPrefixDdt = new uint[imageInfo.Sectors];

                            sector = new byte[2048];

                            if(ArrayHelpers.ArrayIsNullOrEmpty(data))
                            {
                                sectorPrefixDdt[sectorAddress] = (uint)CdFixFlags.NotDumped;
                                sectorSuffixDdt[sectorAddress] = (uint)CdFixFlags.NotDumped;

                                return WriteSector(sector, sectorAddress);
                            }

                            prefixCorrect = true;

                            if(data[0x00] != 0x00 ||
                               data[0x01] != 0xFF ||
                               data[0x02] != 0xFF ||
                               data[0x03] != 0xFF ||
                               data[0x04] != 0xFF ||
                               data[0x05] != 0xFF ||
                               data[0x06] != 0xFF ||
                               data[0x07] != 0xFF ||
                               data[0x08] != 0xFF ||
                               data[0x09] != 0xFF ||
                               data[0x0A] != 0xFF ||
                               data[0x0B] != 0x00 ||
                               data[0x0F] != 0x01)
                                prefixCorrect = false;

                            if(prefixCorrect)
                            {
                                minute        = ((data[0x0C] >> 4) * 10)                     + (data[0x0C] & 0x0F);
                                second        = ((data[0x0D] >> 4) * 10)                     + (data[0x0D] & 0x0F);
                                frame         = ((data[0x0E] >> 4) * 10)                     + (data[0x0E] & 0x0F);
                                storedLba     = ((minute * 60 * 75) + (second * 75) + frame) - 150;
                                prefixCorrect = storedLba == (int)sectorAddress;
                            }

                            if(prefixCorrect)
                                sectorPrefixDdt[sectorAddress] = (uint)CdFixFlags.Correct;
                            else
                            {
                                if((sectorPrefixDdt[sectorAddress] & CD_DFIX_MASK) > 0)
                                    sectorPrefixMs.Position =
                                        ((sectorPrefixDdt[sectorAddress] & CD_DFIX_MASK) - 1) * 16;
                                else
                                    sectorPrefixMs.Seek(0, SeekOrigin.End);

                                sectorPrefixDdt[sectorAddress] = (uint)((sectorPrefixMs.Position / 16) + 1);
                                sectorPrefixMs.Write(data, 0, 16);
                            }

                            bool correct = SuffixIsCorrect(data);

                            if(correct)
                                sectorSuffixDdt[sectorAddress] = (uint)CdFixFlags.Correct;
                            else
                            {
                                if((sectorSuffixDdt[sectorAddress] & CD_DFIX_MASK) > 0)
                                    sectorSuffixMs.Position =
                                        ((sectorSuffixDdt[sectorAddress] & CD_DFIX_MASK) - 1) * 288;
                                else
                                    sectorSuffixMs.Seek(0, SeekOrigin.End);

                                sectorSuffixDdt[sectorAddress] = (uint)((sectorSuffixMs.Position / 288) + 1);

                                sectorSuffixMs.Write(data, 2064, 288);
                            }

                            Array.Copy(data, 16, sector, 0, 2048);

                            return WriteSector(sector, sectorAddress);
                        case TrackType.CdMode2Formless:
                        case TrackType.CdMode2Form1:
                        case TrackType.CdMode2Form2:
                            if(sectorPrefix != null &&
                               sectorSuffix != null)
                            {
                                sector = new byte[2336];
                                Array.Copy(data, 0, sectorPrefix, (int)sectorAddress * 16, 16);
                                Array.Copy(data, 16, sector, 0, 2336);

                                return WriteSector(sector, sectorAddress);
                            }

                            if(sectorSuffixMs == null)
                                sectorSuffixMs = new MemoryStream();

                            if(sectorPrefixMs == null)
                                sectorPrefixMs = new MemoryStream();

                            if(sectorSuffixDdt == null)
                            {
                                sectorSuffixDdt = new uint[imageInfo.Sectors];
                                EccInit();
                            }

                            if(sectorPrefixDdt == null)
                                sectorPrefixDdt = new uint[imageInfo.Sectors];

                            sector = new byte[2328];

                            if(ArrayHelpers.ArrayIsNullOrEmpty(data))
                            {
                                sectorPrefixDdt[sectorAddress] = (uint)CdFixFlags.NotDumped;

                                return WriteSector(sector, sectorAddress);
                            }

                            prefixCorrect = true;

                            if(data[0x00] != 0x00 ||
                               data[0x01] != 0xFF ||
                               data[0x02] != 0xFF ||
                               data[0x03] != 0xFF ||
                               data[0x04] != 0xFF ||
                               data[0x05] != 0xFF ||
                               data[0x06] != 0xFF ||
                               data[0x07] != 0xFF ||
                               data[0x08] != 0xFF ||
                               data[0x09] != 0xFF ||
                               data[0x0A] != 0xFF ||
                               data[0x0B] != 0x00 ||
                               data[0x0F] != 0x02)
                                prefixCorrect = false;

                            if(prefixCorrect)
                            {
                                minute        = ((data[0x0C] >> 4) * 10)                     + (data[0x0C] & 0x0F);
                                second        = ((data[0x0D] >> 4) * 10)                     + (data[0x0D] & 0x0F);
                                frame         = ((data[0x0E] >> 4) * 10)                     + (data[0x0E] & 0x0F);
                                storedLba     = ((minute * 60 * 75) + (second * 75) + frame) - 150;
                                prefixCorrect = storedLba == (int)sectorAddress;
                            }

                            if(prefixCorrect)
                                sectorPrefixDdt[sectorAddress] = (uint)CdFixFlags.Correct;
                            else
                            {
                                if((sectorPrefixDdt[sectorAddress] & CD_DFIX_MASK) > 0)
                                    sectorPrefixMs.Position =
                                        ((sectorPrefixDdt[sectorAddress] & CD_DFIX_MASK) - 1) * 16;
                                else
                                    sectorPrefixMs.Seek(0, SeekOrigin.End);

                                sectorPrefixDdt[sectorAddress] = (uint)((sectorPrefixMs.Position / 16) + 1);

                                sectorPrefixMs.Write(data, 0, 16);
                            }

                            if(mode2Subheaders == null)
                                mode2Subheaders = new byte[imageInfo.Sectors * 8];

                            bool form2 = (data[18] & 0x20) == 0x20 || (data[22] & 0x20) == 0x20;

                            if(form2)
                            {
                                bool correctEdc  = false;
                                uint computedEdc = ComputeEdc(0, data, 0x91C, 0x10);
                                uint edc         = BitConverter.ToUInt32(data, 0x92C);
                                correctEdc = computedEdc == edc;

                                sector = new byte[2324];

                                if(sectorSuffixDdt == null)
                                    sectorSuffixDdt = new uint[imageInfo.Sectors];

                                Array.Copy(data, 24, sector, 0, 2324);

                                if(correctEdc)
                                {
                                    sectorSuffixDdt[sectorAddress] = (uint)CdFixFlags.Mode2Form2Ok;
                                }
                                else if(BitConverter.ToUInt32(data, 0x92C) == 0)
                                {
                                    sectorSuffixDdt[sectorAddress] = (uint)CdFixFlags.Mode2Form2NoCrc;
                                }
                                else
                                {
                                    if((sectorSuffixDdt[sectorAddress] & CD_DFIX_MASK) > 0)
                                        sectorSuffixMs.Position =
                                            ((sectorSuffixDdt[sectorAddress] & CD_DFIX_MASK) - 1) * 288;
                                    else
                                        sectorSuffixMs.Seek(0, SeekOrigin.End);

                                    sectorSuffixDdt[sectorAddress] = (uint)((sectorSuffixMs.Position / 288) + 1);

                                    sectorSuffixMs.Write(data, 2348, 4);
                                }
                            }
                            else
                            {
                                bool correctEcc = SuffixIsCorrectMode2(data);
                                bool correctEdc = false;

                                uint computedEdc = ComputeEdc(0, data, 0x808, 0x10);
                                uint edc         = BitConverter.ToUInt32(data, 0x818);
                                correctEdc = computedEdc == edc;

                                sector = new byte[2048];
                                Array.Copy(data, 24, sector, 0, 2048);

                                if(correctEcc && correctEdc)
                                {
                                    if(sectorSuffixDdt == null)
                                        sectorSuffixDdt = new uint[imageInfo.Sectors];

                                    sectorSuffixDdt[sectorAddress] = (uint)CdFixFlags.Mode2Form1Ok;
                                }
                                else
                                {
                                    if((sectorSuffixDdt[sectorAddress] & CD_DFIX_MASK) > 0)
                                        sectorSuffixMs.Position =
                                            ((sectorSuffixDdt[sectorAddress] & CD_DFIX_MASK) - 1) * 288;
                                    else
                                        sectorSuffixMs.Seek(0, SeekOrigin.End);

                                    sectorSuffixDdt[sectorAddress] = (uint)((sectorSuffixMs.Position / 288) + 1);

                                    sectorSuffixMs.Write(data, 2072, 280);
                                }
                            }

                            Array.Copy(data, 16, mode2Subheaders, (int)sectorAddress * 8, 8);

                            return WriteSector(sector, sectorAddress);
                    }

                    break;
                case XmlMediaType.BlockMedia:
                    switch(imageInfo.MediaType)
                    {
                        // Split user data from Apple tags
                        case MediaType.AppleFileWare:
                        case MediaType.AppleProfile:
                        case MediaType.AppleSonyDS:
                        case MediaType.AppleSonySS:
                        case MediaType.AppleWidget:
                        case MediaType.PriamDataTower:
                            byte[] oldTag;
                            byte[] newTag;

                            switch(data.Length - 512)
                            {
                                // Sony tag, convert to Profile
                                case 12 when imageInfo.MediaType == MediaType.AppleProfile ||
                                             imageInfo.MediaType == MediaType.AppleFileWare:
                                    oldTag = new byte[12];
                                    Array.Copy(data, 512, oldTag, 0, 12);
                                    newTag = LisaTag.DecodeSonyTag(oldTag)?.ToProfile().GetBytes();

                                    break;

                                // Sony tag, convert to Priam
                                case 12 when imageInfo.MediaType == MediaType.PriamDataTower:
                                    oldTag = new byte[12];
                                    Array.Copy(data, 512, oldTag, 0, 12);
                                    newTag = LisaTag.DecodeSonyTag(oldTag)?.ToPriam().GetBytes();

                                    break;

                                // Sony tag, copy to Sony
                                case 12 when imageInfo.MediaType == MediaType.AppleSonySS ||
                                             imageInfo.MediaType == MediaType.AppleSonySS:
                                    newTag = new byte[12];
                                    Array.Copy(data, 512, newTag, 0, 12);

                                    break;

                                // Profile tag, copy to Profile
                                case 20 when imageInfo.MediaType == MediaType.AppleProfile ||
                                             imageInfo.MediaType == MediaType.AppleFileWare:
                                    newTag = new byte[20];
                                    Array.Copy(data, 512, newTag, 0, 20);

                                    break;

                                // Profile tag, convert to Priam
                                case 20 when imageInfo.MediaType == MediaType.PriamDataTower:
                                    oldTag = new byte[20];
                                    Array.Copy(data, 512, oldTag, 0, 20);
                                    newTag = LisaTag.DecodeProfileTag(oldTag)?.ToPriam().GetBytes();

                                    break;

                                // Profile tag, convert to Sony
                                case 20 when imageInfo.MediaType == MediaType.AppleSonySS ||
                                             imageInfo.MediaType == MediaType.AppleSonySS:
                                    oldTag = new byte[20];
                                    Array.Copy(data, 512, oldTag, 0, 20);
                                    newTag = LisaTag.DecodeProfileTag(oldTag)?.ToSony().GetBytes();

                                    break;

                                // Priam tag, convert to Profile
                                case 24 when imageInfo.MediaType == MediaType.AppleProfile ||
                                             imageInfo.MediaType == MediaType.AppleFileWare:
                                    oldTag = new byte[24];
                                    Array.Copy(data, 512, oldTag, 0, 24);
                                    newTag = LisaTag.DecodePriamTag(oldTag)?.ToProfile().GetBytes();

                                    break;

                                // Priam tag, copy to Priam
                                case 12 when imageInfo.MediaType == MediaType.PriamDataTower:
                                    newTag = new byte[24];
                                    Array.Copy(data, 512, newTag, 0, 24);

                                    break;

                                // Priam tag, convert to Sony
                                case 24 when imageInfo.MediaType == MediaType.AppleSonySS ||
                                             imageInfo.MediaType == MediaType.AppleSonySS:
                                    oldTag = new byte[24];
                                    Array.Copy(data, 512, oldTag, 0, 24);
                                    newTag = LisaTag.DecodePriamTag(oldTag)?.ToSony().GetBytes();

                                    break;
                                case 0:
                                    newTag = null;

                                    break;
                                default:
                                    ErrorMessage = "Incorrect data size";

                                    return false;
                            }

                            sector = new byte[512];
                            Array.Copy(data, 0, sector, 0, 512);

                            if(newTag == null)
                                return WriteSector(sector, sectorAddress);

                            if(sectorSubchannel == null)
                                sectorSubchannel = new byte[newTag.Length * (int)imageInfo.Sectors];

                            Array.Copy(newTag, 0, sectorSubchannel, newTag.Length * (int)sectorAddress, newTag.Length);

                            return WriteSector(sector, sectorAddress);
                    }

                    break;
            }

            ErrorMessage = "Unknown long sector type, cannot write.";

            return false;
        }

        public bool WriteSectorsLong(byte[] data, ulong sectorAddress, uint length)
        {
            byte[] sector;

            switch(imageInfo.XmlMediaType)
            {
                case XmlMediaType.OpticalDisc:
                    Track track =
                        Tracks.FirstOrDefault(trk => sectorAddress >= trk.TrackStartSector &&
                                                     sectorAddress <= trk.TrackEndSector);

                    if(track.TrackSequence    == 0 &&
                       track.TrackStartSector == 0 &&
                       track.TrackEndSector   == 0)
                    {
                        ErrorMessage = $"Can't found track containing {sectorAddress}";

                        return false;
                    }

                    if(data.Length % 2352 != 0)
                    {
                        ErrorMessage = "Incorrect data size";

                        return false;
                    }

                    if(sectorAddress + length > track.TrackEndSector + 1)
                    {
                        ErrorMessage = "Can't cross tracks";

                        return false;
                    }

                    sector = new byte[2352];

                    for(uint i = 0; i < length; i++)
                    {
                        Array.Copy(data, 2352 * i, sector, 0, 2352);

                        if(!WriteSectorLong(sector, sectorAddress + i))
                            return false;
                    }

                    ErrorMessage = "";

                    return true;
                case XmlMediaType.BlockMedia:
                    switch(imageInfo.MediaType)
                    {
                        case MediaType.AppleFileWare:
                        case MediaType.AppleProfile:
                        case MediaType.AppleSonyDS:
                        case MediaType.AppleSonySS:
                        case MediaType.AppleWidget:
                        case MediaType.PriamDataTower:
                            int sectorSize = 0;

                            if(data.Length % 524 == 0)
                                sectorSize = 524;
                            else if(data.Length % 532 == 0)
                                sectorSize = 532;
                            else if(data.Length % 536 == 0)
                                sectorSize = 536;

                            if(sectorSize == 0)
                            {
                                ErrorMessage = "Incorrect data size";

                                return false;
                            }

                            sector = new byte[sectorSize];

                            for(uint i = 0; i < length; i++)
                            {
                                Array.Copy(data, sectorSize * i, sector, 0, sectorSize);

                                if(!WriteSectorLong(sector, sectorAddress + i))
                                    return false;
                            }

                            ErrorMessage = "";

                            return true;
                    }

                    break;
            }

            ErrorMessage = "Unknown long sector type, cannot write.";

            return false;
        }

        public bool SetTracks(List<Track> tracks)
        {
            if(imageInfo.XmlMediaType != XmlMediaType.OpticalDisc)
            {
                ErrorMessage = "Unsupported feature";

                return false;
            }

            if(!IsWriting)
            {
                ErrorMessage = "Tried to write on a non-writable image";

                return false;
            }

            Tracks       = tracks;
            ErrorMessage = "";

            return true;
        }

        public bool Close()
        {
            if(!IsWriting)
            {
                ErrorMessage = "Image is not opened for writing";

                return false;
            }

            // Close current block first
            if(blockStream != null)
            {
                currentBlockHeader.length = currentBlockOffset * currentBlockHeader.sectorSize;
                currentBlockHeader.crc64  = BitConverter.ToUInt64(crc64.Final(), 0);

                var cmpCrc64Context = new Crc64Context();

                byte[] lzmaProperties = new byte[0];

                if(currentBlockHeader.compression == CompressionType.Flac)
                {
                    long remaining = (currentBlockOffset * SAMPLES_PER_SECTOR) % flakeWriter.Settings.BlockSize;

                    // Fill FLAC block
                    if(remaining != 0)
                    {
                        var audioBuffer =
                            new AudioBuffer(AudioPCMConfig.RedBook, new byte[remaining * 4], (int)remaining);

                        flakeWriter.Write(audioBuffer);
                    }

                    // This trick because CUETools.Codecs.Flake closes the underlying stream
                    long   realLength = blockStream.Length;
                    byte[] buffer     = new byte[realLength];
                    flakeWriter.Close();
                    Array.Copy(blockStream.GetBuffer(), 0, buffer, 0, realLength);
                    blockStream = new MemoryStream(buffer);
                }
                else if(currentBlockHeader.compression == CompressionType.Lzma)
                {
                    lzmaProperties = lzmaBlockStream.Properties;
                    lzmaBlockStream.Close();
                    lzmaBlockStream = null;
                    cmpCrc64Context.Update(lzmaProperties);

                    if(blockStream.Length > decompressedStream.Length)
                        currentBlockHeader.compression = CompressionType.None;
                }

                if(currentBlockHeader.compression == CompressionType.None)
                {
                    blockStream                 = decompressedStream;
                    currentBlockHeader.cmpCrc64 = currentBlockHeader.crc64;
                }
                else
                {
                    cmpCrc64Context.Update(blockStream.ToArray());
                    currentBlockHeader.cmpCrc64 = BitConverter.ToUInt64(cmpCrc64Context.Final(), 0);
                }

                currentBlockHeader.cmpLength = (uint)blockStream.Length;

                if(currentBlockHeader.compression == CompressionType.Lzma)
                    currentBlockHeader.cmpLength += LZMA_PROPERTIES_LENGTH;

                index.Add(new IndexEntry
                {
                    blockType = BlockType.DataBlock, dataType = DataType.UserData, offset = (ulong)imageStream.Position
                });

                structureBytes = new byte[Marshal.SizeOf<BlockHeader>()];
                MemoryMarshal.Write(structureBytes, ref currentBlockHeader);
                imageStream.Write(structureBytes, 0, structureBytes.Length);
                structureBytes = null;

                if(currentBlockHeader.compression == CompressionType.Lzma)
                    imageStream.Write(lzmaProperties, 0, lzmaProperties.Length);

                imageStream.Write(blockStream.ToArray(), 0, (int)blockStream.Length);
                blockStream.Close();
                blockStream = null;
            }

            if(deduplicate)
                AaruConsole.DebugWriteLine("Aaru Format plugin", "Of {0} sectors written, {1} are unique ({2:P})",
                                           writtenSectors, deduplicationTable.Count,
                                           (double)deduplicationTable.Count / writtenSectors);

            IndexEntry idxEntry;

            // Write media tag blocks
            foreach(KeyValuePair<MediaTagType, byte[]> mediaTag in mediaTags)
            {
                DataType dataType = GetDataTypeForMediaTag(mediaTag.Key);

                if(mediaTag.Value is null)
                {
                    AaruConsole.ErrorWriteLine("Tag type {0} is null, skipping...", dataType);

                    continue;
                }

                idxEntry = new IndexEntry
                {
                    blockType = BlockType.DataBlock, dataType = dataType, offset = (ulong)imageStream.Position
                };

                AaruConsole.DebugWriteLine("Aaru Format plugin", "Writing tag type {0} to position {1}", mediaTag.Key,
                                           idxEntry.offset);

                Crc64Context.Data(mediaTag.Value, out byte[] tagCrc);

                var tagBlock = new BlockHeader
                {
                    identifier = BlockType.DataBlock, type = dataType, length = (uint)mediaTag.Value.Length,
                    crc64      = BitConverter.ToUInt64(tagCrc, 0)
                };

                blockStream = new MemoryStream();

                byte[] lzmaProperties =
                    CompressDataToStreamWithLZMA(mediaTag.Value, lzmaEncoderProperties, blockStream, false);

                byte[] tagData;

                // Not compressible
                if(blockStream.Length + LZMA_PROPERTIES_LENGTH >= mediaTag.Value.Length)
                {
                    tagBlock.cmpLength   = tagBlock.length;
                    tagBlock.cmpCrc64    = tagBlock.crc64;
                    tagData              = mediaTag.Value;
                    tagBlock.compression = CompressionType.None;
                }
                else
                {
                    tagData = blockStream.ToArray();
                    var crc64Ctx = new Crc64Context();
                    crc64Ctx.Update(lzmaProperties);
                    crc64Ctx.Update(tagData);
                    tagCrc               = crc64Ctx.Final();
                    tagBlock.cmpLength   = (uint)tagData.Length + LZMA_PROPERTIES_LENGTH;
                    tagBlock.cmpCrc64    = BitConverter.ToUInt64(tagCrc, 0);
                    tagBlock.compression = CompressionType.Lzma;
                }

                blockStream.Close();
                blockStream = null;

                structureBytes = new byte[Marshal.SizeOf<BlockHeader>()];
                MemoryMarshal.Write(structureBytes, ref tagBlock);
                imageStream.Write(structureBytes, 0, structureBytes.Length);

                if(tagBlock.compression == CompressionType.Lzma)
                    imageStream.Write(lzmaProperties, 0, lzmaProperties.Length);

                imageStream.Write(tagData, 0, tagData.Length);

                index.RemoveAll(t => t.blockType == BlockType.DataBlock && t.dataType == dataType);

                index.Add(idxEntry);
            }

            // If we have set the geometry block, write it
            if(geometryBlock.identifier == BlockType.GeometryBlock)
            {
                idxEntry = new IndexEntry
                {
                    blockType = BlockType.GeometryBlock, dataType = DataType.NoData,
                    offset    = (ulong)imageStream.Position
                };

                AaruConsole.DebugWriteLine("Aaru Format plugin", "Writing geometry block to position {0}",
                                           idxEntry.offset);

                structureBytes = new byte[Marshal.SizeOf<GeometryBlock>()];
                MemoryMarshal.Write(structureBytes, ref geometryBlock);
                imageStream.Write(structureBytes, 0, structureBytes.Length);

                index.RemoveAll(t => t.blockType == BlockType.GeometryBlock && t.dataType == DataType.NoData);

                index.Add(idxEntry);
            }

            // If we have dump hardware, write it
            if(DumpHardware != null)
            {
                var dumpMs = new MemoryStream();

                foreach(DumpHardwareType dump in DumpHardware)
                {
                    byte[] dumpManufacturer            = null;
                    byte[] dumpModel                   = null;
                    byte[] dumpRevision                = null;
                    byte[] dumpFirmware                = null;
                    byte[] dumpSerial                  = null;
                    byte[] dumpSoftwareName            = null;
                    byte[] dumpSoftwareVersion         = null;
                    byte[] dumpSoftwareOperatingSystem = null;

                    if(!string.IsNullOrWhiteSpace(dump.Manufacturer))
                        dumpManufacturer = Encoding.UTF8.GetBytes(dump.Manufacturer);

                    if(!string.IsNullOrWhiteSpace(dump.Model))
                        dumpModel = Encoding.UTF8.GetBytes(dump.Model);

                    if(!string.IsNullOrWhiteSpace(dump.Revision))
                        dumpRevision = Encoding.UTF8.GetBytes(dump.Revision);

                    if(!string.IsNullOrWhiteSpace(dump.Firmware))
                        dumpFirmware = Encoding.UTF8.GetBytes(dump.Firmware);

                    if(!string.IsNullOrWhiteSpace(dump.Serial))
                        dumpSerial = Encoding.UTF8.GetBytes(dump.Serial);

                    if(dump.Software != null)
                    {
                        if(!string.IsNullOrWhiteSpace(dump.Software.Name))
                            dumpSoftwareName = Encoding.UTF8.GetBytes(dump.Software.Name);

                        if(!string.IsNullOrWhiteSpace(dump.Software.Version))
                            dumpSoftwareVersion = Encoding.UTF8.GetBytes(dump.Software.Version);

                        if(!string.IsNullOrWhiteSpace(dump.Software.OperatingSystem))
                            dumpSoftwareOperatingSystem = Encoding.UTF8.GetBytes(dump.Software.OperatingSystem);
                    }

                    var dumpEntry = new DumpHardwareEntry
                    {
                        manufacturerLength            = (uint)(dumpManufacturer?.Length            + 1 ?? 0),
                        modelLength                   = (uint)(dumpModel?.Length                   + 1 ?? 0),
                        revisionLength                = (uint)(dumpRevision?.Length                + 1 ?? 0),
                        firmwareLength                = (uint)(dumpFirmware?.Length                + 1 ?? 0),
                        serialLength                  = (uint)(dumpSerial?.Length                  + 1 ?? 0),
                        softwareNameLength            = (uint)(dumpSoftwareName?.Length            + 1 ?? 0),
                        softwareVersionLength         = (uint)(dumpSoftwareVersion?.Length         + 1 ?? 0),
                        softwareOperatingSystemLength = (uint)(dumpSoftwareOperatingSystem?.Length + 1 ?? 0),
                        extents                       = (uint)dump.Extents.Length
                    };

                    structureBytes = new byte[Marshal.SizeOf<DumpHardwareEntry>()];
                    MemoryMarshal.Write(structureBytes, ref dumpEntry);
                    dumpMs.Write(structureBytes, 0, structureBytes.Length);

                    if(dumpManufacturer != null)
                    {
                        dumpMs.Write(dumpManufacturer, 0, dumpManufacturer.Length);
                        dumpMs.WriteByte(0);
                    }

                    if(dumpModel != null)
                    {
                        dumpMs.Write(dumpModel, 0, dumpModel.Length);
                        dumpMs.WriteByte(0);
                    }

                    if(dumpRevision != null)
                    {
                        dumpMs.Write(dumpRevision, 0, dumpRevision.Length);
                        dumpMs.WriteByte(0);
                    }

                    if(dumpFirmware != null)
                    {
                        dumpMs.Write(dumpFirmware, 0, dumpFirmware.Length);
                        dumpMs.WriteByte(0);
                    }

                    if(dumpSerial != null)
                    {
                        dumpMs.Write(dumpSerial, 0, dumpSerial.Length);
                        dumpMs.WriteByte(0);
                    }

                    if(dumpSoftwareName != null)
                    {
                        dumpMs.Write(dumpSoftwareName, 0, dumpSoftwareName.Length);
                        dumpMs.WriteByte(0);
                    }

                    if(dumpSoftwareVersion != null)
                    {
                        dumpMs.Write(dumpSoftwareVersion, 0, dumpSoftwareVersion.Length);
                        dumpMs.WriteByte(0);
                    }

                    if(dumpSoftwareOperatingSystem != null)
                    {
                        dumpMs.Write(dumpSoftwareOperatingSystem, 0, dumpSoftwareOperatingSystem.Length);
                        dumpMs.WriteByte(0);
                    }

                    foreach(ExtentType extent in dump.Extents)
                    {
                        dumpMs.Write(BitConverter.GetBytes(extent.Start), 0, sizeof(ulong));
                        dumpMs.Write(BitConverter.GetBytes(extent.End), 0, sizeof(ulong));
                    }
                }

                idxEntry = new IndexEntry
                {
                    blockType = BlockType.DumpHardwareBlock, dataType = DataType.NoData,
                    offset    = (ulong)imageStream.Position
                };

                AaruConsole.DebugWriteLine("Aaru Format plugin", "Writing dump hardware block to position {0}",
                                           idxEntry.offset);

                Crc64Context.Data(dumpMs.ToArray(), out byte[] dumpCrc);

                var dumpBlock = new DumpHardwareHeader
                {
                    identifier = BlockType.DumpHardwareBlock, entries      = (ushort)DumpHardware.Count,
                    crc64      = BitConverter.ToUInt64(dumpCrc, 0), length = (uint)dumpMs.Length
                };

                structureBytes = new byte[Marshal.SizeOf<DumpHardwareHeader>()];
                MemoryMarshal.Write(structureBytes, ref dumpBlock);
                imageStream.Write(structureBytes, 0, structureBytes.Length);
                imageStream.Write(dumpMs.ToArray(), 0, (int)dumpMs.Length);

                index.RemoveAll(t => t.blockType == BlockType.DumpHardwareBlock && t.dataType == DataType.NoData);

                index.Add(idxEntry);
            }

            // If we have CICM XML metadata, write it
            if(CicmMetadata != null)
            {
                var cicmMs = new MemoryStream();
                var xmlSer = new XmlSerializer(typeof(CICMMetadataType));
                xmlSer.Serialize(cicmMs, CicmMetadata);

                idxEntry = new IndexEntry
                {
                    blockType = BlockType.CicmBlock, dataType = DataType.NoData, offset = (ulong)imageStream.Position
                };

                AaruConsole.DebugWriteLine("Aaru Format plugin", "Writing CICM XML block to position {0}",
                                           idxEntry.offset);

                var cicmBlock = new CicmMetadataBlock
                {
                    identifier = BlockType.CicmBlock, length = (uint)cicmMs.Length
                };

                structureBytes = new byte[Marshal.SizeOf<CicmMetadataBlock>()];
                MemoryMarshal.Write(structureBytes, ref cicmBlock);
                imageStream.Write(structureBytes, 0, structureBytes.Length);
                imageStream.Write(cicmMs.ToArray(), 0, (int)cicmMs.Length);

                index.RemoveAll(t => t.blockType == BlockType.CicmBlock && t.dataType == DataType.NoData);

                index.Add(idxEntry);
            }

            // If we have checksums, write it to disk
            if(md5Provider     != null ||
               sha1Provider    != null ||
               sha256Provider  != null ||
               spamsumProvider != null)
            {
                var chkMs = new MemoryStream();

                var chkHeader = new ChecksumHeader
                {
                    identifier = BlockType.ChecksumBlock
                };

                if(md5Provider != null)
                {
                    byte[] md5 = md5Provider.Final();

                    var md5Entry = new ChecksumEntry
                    {
                        type = ChecksumAlgorithm.Md5, length = (uint)md5.Length
                    };

                    structureBytes = new byte[Marshal.SizeOf<ChecksumEntry>()];
                    MemoryMarshal.Write(structureBytes, ref md5Entry);
                    chkMs.Write(structureBytes, 0, structureBytes.Length);
                    chkMs.Write(md5, 0, md5.Length);
                    chkHeader.entries++;
                }

                if(sha1Provider != null)
                {
                    byte[] sha1 = sha1Provider.Final();

                    var sha1Entry = new ChecksumEntry
                    {
                        type = ChecksumAlgorithm.Sha1, length = (uint)sha1.Length
                    };

                    structureBytes = new byte[Marshal.SizeOf<ChecksumEntry>()];
                    MemoryMarshal.Write(structureBytes, ref sha1Entry);
                    chkMs.Write(structureBytes, 0, structureBytes.Length);
                    chkMs.Write(sha1, 0, sha1.Length);
                    chkHeader.entries++;
                }

                if(sha256Provider != null)
                {
                    byte[] sha256 = sha256Provider.Final();

                    var sha256Entry = new ChecksumEntry
                    {
                        type = ChecksumAlgorithm.Sha256, length = (uint)sha256.Length
                    };

                    structureBytes = new byte[Marshal.SizeOf<ChecksumEntry>()];
                    MemoryMarshal.Write(structureBytes, ref sha256Entry);
                    chkMs.Write(structureBytes, 0, structureBytes.Length);
                    chkMs.Write(sha256, 0, sha256.Length);
                    chkHeader.entries++;
                }

                if(spamsumProvider != null)
                {
                    byte[] spamsum = Encoding.ASCII.GetBytes(spamsumProvider.End());

                    var spamsumEntry = new ChecksumEntry
                    {
                        type = ChecksumAlgorithm.SpamSum, length = (uint)spamsum.Length
                    };

                    structureBytes = new byte[Marshal.SizeOf<ChecksumEntry>()];
                    MemoryMarshal.Write(structureBytes, ref spamsumEntry);
                    chkMs.Write(structureBytes, 0, structureBytes.Length);
                    chkMs.Write(spamsum, 0, spamsum.Length);
                    chkHeader.entries++;
                }

                if(chkHeader.entries > 0)
                {
                    chkHeader.length = (uint)chkMs.Length;

                    idxEntry = new IndexEntry
                    {
                        blockType = BlockType.ChecksumBlock, dataType = DataType.NoData,
                        offset    = (ulong)imageStream.Position
                    };

                    AaruConsole.DebugWriteLine("Aaru Format plugin", "Writing checksum block to position {0}",
                                               idxEntry.offset);

                    structureBytes = new byte[Marshal.SizeOf<ChecksumHeader>()];
                    MemoryMarshal.Write(structureBytes, ref chkHeader);
                    imageStream.Write(structureBytes, 0, structureBytes.Length);
                    imageStream.Write(chkMs.ToArray(), 0, (int)chkMs.Length);

                    index.RemoveAll(t => t.blockType == BlockType.ChecksumBlock && t.dataType == DataType.NoData);

                    index.Add(idxEntry);
                }
            }

            if(IsTape)
            {
                ulong latestBlock = tapeDdt.Max(b => b.Key);

                userDataDdt = new ulong[latestBlock + 1];

                foreach(KeyValuePair<ulong, ulong> block in tapeDdt)
                    userDataDdt[block.Key] = block.Value;

                inMemoryDdt = true;
                tapeDdt.Clear();

                idxEntry = new IndexEntry
                {
                    blockType = BlockType.TapePartitionBlock, dataType = DataType.UserData,
                    offset    = (ulong)imageStream.Position
                };

                AaruConsole.DebugWriteLine("Aaru Format plugin", "Writing tape partitions to position {0}",
                                           idxEntry.offset);

                TapePartitionEntry[] tapePartitionEntries = new TapePartitionEntry[TapePartitions.Count];

                for(int t = 0; t < TapePartitions.Count; t++)
                {
                    tapePartitionEntries[t]            = new TapePartitionEntry();
                    tapePartitionEntries[t].Number     = TapePartitions[t].Number;
                    tapePartitionEntries[t].FirstBlock = TapePartitions[t].FirstBlock;
                    tapePartitionEntries[t].LastBlock  = TapePartitions[t].LastBlock;
                }

                byte[] tapePartitionEntriesData =
                    MemoryMarshal.Cast<TapePartitionEntry, byte>(tapePartitionEntries).ToArray();

                var tapePartitionHeader = new TapePartitionHeader();
                tapePartitionHeader.identifier = BlockType.TapePartitionBlock;
                tapePartitionHeader.entries    = (byte)tapePartitionEntries.Length;
                tapePartitionHeader.length     = (ulong)tapePartitionEntriesData.Length;

                crc64 = new Crc64Context();
                crc64.Update(tapePartitionEntriesData);
                tapePartitionHeader.crc64 = BitConverter.ToUInt64(crc64.Final(), 0);

                structureBytes = new byte[Marshal.SizeOf<TapePartitionHeader>()];
                MemoryMarshal.Write(structureBytes, ref tapePartitionHeader);
                imageStream.Write(structureBytes, 0, structureBytes.Length);
                structureBytes = null;
                imageStream.Write(tapePartitionEntriesData, 0, tapePartitionEntriesData.Length);

                index.RemoveAll(t => t.blockType == BlockType.TapePartitionBlock && t.dataType == DataType.UserData);
                index.Add(idxEntry);

                idxEntry = new IndexEntry
                {
                    blockType = BlockType.TapeFileBlock, dataType = DataType.UserData,
                    offset    = (ulong)imageStream.Position
                };

                AaruConsole.DebugWriteLine("Aaru Format plugin", "Writing tape files to position {0}", idxEntry.offset);

                TapeFileEntry[] tapeFileEntries = new TapeFileEntry[Files.Count];

                for(int t = 0; t < Files.Count; t++)
                    tapeFileEntries[t] = new TapeFileEntry
                    {
                        File = Files[t].File, FirstBlock = Files[t].FirstBlock, LastBlock = Files[t].LastBlock
                    };

                byte[] tapeFileEntriesData = MemoryMarshal.Cast<TapeFileEntry, byte>(tapeFileEntries).ToArray();

                var tapeFileHeader = new TapeFileHeader
                {
                    identifier = BlockType.TapeFileBlock, entries = (uint)tapeFileEntries.Length,
                    length     = (ulong)tapeFileEntriesData.Length
                };

                crc64 = new Crc64Context();
                crc64.Update(tapeFileEntriesData);
                tapeFileHeader.crc64 = BitConverter.ToUInt64(crc64.Final(), 0);

                structureBytes = new byte[Marshal.SizeOf<TapeFileHeader>()];
                MemoryMarshal.Write(structureBytes, ref tapeFileHeader);
                imageStream.Write(structureBytes, 0, structureBytes.Length);
                structureBytes = null;
                imageStream.Write(tapeFileEntriesData, 0, tapeFileEntriesData.Length);

                index.RemoveAll(t => t.blockType == BlockType.TapeFileBlock && t.dataType == DataType.UserData);
                index.Add(idxEntry);
            }

            // If the DDT is in-memory, write it to disk
            if(inMemoryDdt)
            {
                idxEntry = new IndexEntry
                {
                    blockType = BlockType.DeDuplicationTable, dataType = DataType.UserData,
                    offset    = (ulong)imageStream.Position
                };

                AaruConsole.DebugWriteLine("Aaru Format plugin", "Writing user data DDT to position {0}",
                                           idxEntry.offset);

                var ddtHeader = new DdtHeader
                {
                    identifier  = BlockType.DeDuplicationTable, type = DataType.UserData,
                    compression = CompressionType.Lzma, shift        = shift, entries = (ulong)userDataDdt.LongLength,
                    length      = (ulong)(userDataDdt.LongLength * sizeof(ulong))
                };

                blockStream = new MemoryStream();
                var userDataDdtStream = new MemoryStream();
                crc64 = new Crc64Context();
                byte[] ddtEntries = MemoryMarshal.Cast<ulong, byte>(userDataDdt).ToArray();
                crc64.Update(ddtEntries);
                userDataDdtStream.Write(ddtEntries, 0, ddtEntries.Length);

                byte[] lzmaProperties =
                    CompressDataToStreamWithLZMA(userDataDdtStream.ToArray(), lzmaEncoderProperties, blockStream,
                                                 false);

                userDataDdtStream.Close();
                ddtHeader.cmpLength = (uint)blockStream.Length + LZMA_PROPERTIES_LENGTH;
                var cmpCrc64Context = new Crc64Context();
                cmpCrc64Context.Update(lzmaProperties);
                cmpCrc64Context.Update(blockStream.ToArray());
                ddtHeader.cmpCrc64 = BitConverter.ToUInt64(cmpCrc64Context.Final(), 0);

                structureBytes = new byte[Marshal.SizeOf<DdtHeader>()];
                MemoryMarshal.Write(structureBytes, ref ddtHeader);
                imageStream.Write(structureBytes, 0, structureBytes.Length);
                structureBytes = null;
                imageStream.Write(lzmaProperties, 0, lzmaProperties.Length);
                imageStream.Write(blockStream.ToArray(), 0, (int)blockStream.Length);
                blockStream.Close();
                blockStream = null;

                index.RemoveAll(t => t.blockType == BlockType.DeDuplicationTable && t.dataType == DataType.UserData);

                index.Add(idxEntry);
            }

            // Write the sector prefix, suffix and subchannels if present
            switch(imageInfo.XmlMediaType)
            {
                case XmlMediaType.OpticalDisc when Tracks != null && Tracks.Count > 0:
                    DateTime startCompress;
                    DateTime endCompress;

                    // Old format
                    if(sectorPrefix != null &&
                       sectorSuffix != null)
                    {
                        idxEntry = new IndexEntry
                        {
                            blockType = BlockType.DataBlock, dataType = DataType.CdSectorPrefix,
                            offset    = (ulong)imageStream.Position
                        };

                        AaruConsole.DebugWriteLine("Aaru Format plugin",
                                                   "Writing CD sector prefix block to position {0}", idxEntry.offset);

                        Crc64Context.Data(sectorPrefix, out byte[] blockCrc);

                        var prefixBlock = new BlockHeader
                        {
                            identifier = BlockType.DataBlock, type        = DataType.CdSectorPrefix,
                            length     = (uint)sectorPrefix.Length, crc64 = BitConverter.ToUInt64(blockCrc, 0),
                            sectorSize = 16
                        };

                        byte[] lzmaProperties = null;

                        if(!compress)
                        {
                            prefixBlock.compression = CompressionType.None;
                            prefixBlock.cmpCrc64    = prefixBlock.crc64;
                            prefixBlock.cmpLength   = prefixBlock.length;
                            blockStream             = new MemoryStream(sectorPrefix);
                        }
                        else
                        {
                            startCompress = DateTime.Now;
                            blockStream   = new MemoryStream();

                            lzmaProperties =
                                CompressDataToStreamWithLZMA(sectorPrefix, lzmaEncoderProperties, blockStream, false);

                            var cmpCrc = new Crc64Context();
                            cmpCrc.Update(lzmaProperties);
                            cmpCrc.Update(blockStream.ToArray());
                            blockCrc                = cmpCrc.Final();
                            prefixBlock.cmpLength   = (uint)blockStream.Length + LZMA_PROPERTIES_LENGTH;
                            prefixBlock.cmpCrc64    = BitConverter.ToUInt64(blockCrc, 0);
                            prefixBlock.compression = CompressionType.Lzma;

                            endCompress = DateTime.Now;

                            AaruConsole.DebugWriteLine("Aaru Format plugin", "Took {0} seconds to compress prefix",
                                                       (endCompress - startCompress).TotalSeconds);
                        }

                        structureBytes = new byte[Marshal.SizeOf<BlockHeader>()];
                        MemoryMarshal.Write(structureBytes, ref prefixBlock);
                        imageStream.Write(structureBytes, 0, structureBytes.Length);

                        if(prefixBlock.compression == CompressionType.Lzma)
                            imageStream.Write(lzmaProperties, 0, lzmaProperties.Length);

                        imageStream.Write(blockStream.ToArray(), 0, (int)blockStream.Length);
                        blockStream.Close();
                        blockStream = null;

                        index.RemoveAll(t => t.blockType == BlockType.DataBlock &&
                                             t.dataType  == DataType.CdSectorPrefix);

                        index.Add(idxEntry);

                        idxEntry = new IndexEntry
                        {
                            blockType = BlockType.DataBlock, dataType = DataType.CdSectorSuffix,
                            offset    = (ulong)imageStream.Position
                        };

                        AaruConsole.DebugWriteLine("Aaru Format plugin",
                                                   "Writing CD sector suffix block to position {0}", idxEntry.offset);

                        Crc64Context.Data(sectorSuffix, out blockCrc);

                        prefixBlock = new BlockHeader
                        {
                            identifier = BlockType.DataBlock, type        = DataType.CdSectorSuffix,
                            length     = (uint)sectorSuffix.Length, crc64 = BitConverter.ToUInt64(blockCrc, 0),
                            sectorSize = 288
                        };

                        if(!compress)
                        {
                            prefixBlock.compression = CompressionType.None;
                            prefixBlock.cmpCrc64    = prefixBlock.crc64;
                            prefixBlock.cmpLength   = prefixBlock.length;
                            blockStream             = new MemoryStream(sectorSuffix);
                        }
                        else
                        {
                            startCompress = DateTime.Now;
                            blockStream   = new MemoryStream();

                            lzmaProperties =
                                CompressDataToStreamWithLZMA(sectorSuffix, lzmaEncoderProperties, blockStream, false);

                            var cmpCrc = new Crc64Context();
                            cmpCrc.Update(lzmaProperties);
                            cmpCrc.Update(blockStream.ToArray());
                            blockCrc                = cmpCrc.Final();
                            prefixBlock.cmpLength   = (uint)blockStream.Length + LZMA_PROPERTIES_LENGTH;
                            prefixBlock.cmpCrc64    = BitConverter.ToUInt64(blockCrc, 0);
                            prefixBlock.compression = CompressionType.Lzma;

                            endCompress = DateTime.Now;

                            AaruConsole.DebugWriteLine("Aaru Format plugin", "Took {0} seconds to compress suffix",
                                                       (endCompress - startCompress).TotalSeconds);
                        }

                        structureBytes = new byte[Marshal.SizeOf<BlockHeader>()];
                        MemoryMarshal.Write(structureBytes, ref prefixBlock);
                        imageStream.Write(structureBytes, 0, structureBytes.Length);

                        if(prefixBlock.compression == CompressionType.Lzma)
                            imageStream.Write(lzmaProperties, 0, lzmaProperties.Length);

                        imageStream.Write(blockStream.ToArray(), 0, (int)blockStream.Length);

                        index.RemoveAll(t => t.blockType == BlockType.DataBlock &&
                                             t.dataType  == DataType.CdSectorSuffix);

                        index.Add(idxEntry);
                        blockStream.Close();
                        blockStream = null;
                    }
                    else if(sectorSuffixMs  != null &&
                            sectorSuffixDdt != null &&
                            sectorPrefixMs  != null &&
                            sectorPrefixDdt != null)
                    {
                    #if DEBUG
                        uint notDumpedPrefixes = 0;
                        uint correctPrefixes   = 0;
                        uint writtenPrefixes   = 0;
                        uint notDumpedSuffixes = 0;
                        uint correctSuffixes   = 0;
                        uint writtenSuffixes   = 0;
                        uint correctMode2Form1 = 0;
                        uint correctMode2Form2 = 0;
                        uint emptyMode2Form1   = 0;

                        for(long i = 0; i < sectorPrefixDdt.LongLength; i++)
                            if((sectorPrefixDdt[i] & CD_XFIX_MASK) == (uint)CdFixFlags.NotDumped)
                                notDumpedPrefixes++;
                            else if((sectorPrefixDdt[i] & CD_XFIX_MASK) == (uint)CdFixFlags.Correct)
                                correctPrefixes++;
                            else if((sectorPrefixDdt[i] & CD_DFIX_MASK) > 0)
                                writtenPrefixes++;

                        for(long i = 0; i < sectorPrefixDdt.LongLength; i++)
                            if((sectorSuffixDdt[i] & CD_XFIX_MASK) == (uint)CdFixFlags.NotDumped)
                                notDumpedSuffixes++;
                            else if((sectorSuffixDdt[i] & CD_XFIX_MASK) == (uint)CdFixFlags.Correct)
                                correctSuffixes++;
                            else if((sectorSuffixDdt[i] & CD_XFIX_MASK) == (uint)CdFixFlags.Mode2Form1Ok)
                                correctMode2Form1++;
                            else if((sectorSuffixDdt[i] & CD_XFIX_MASK) == (uint)CdFixFlags.Mode2Form2Ok)
                                correctMode2Form2++;
                            else if((sectorSuffixDdt[i] & CD_XFIX_MASK) == (uint)CdFixFlags.Mode2Form2NoCrc)
                                emptyMode2Form1++;
                            else if((sectorSuffixDdt[i] & CD_DFIX_MASK) > 0)
                                writtenSuffixes++;

                        AaruConsole.DebugWriteLine("Aaru Format plugin",
                                                   "{0} ({1:P}% prefixes are correct, {2} ({3:P}%) prefixes have not been dumped, {4} ({5:P}%) prefixes have been written to image",
                                                   correctPrefixes, correctPrefixes     / imageInfo.Sectors,
                                                   notDumpedPrefixes, notDumpedPrefixes / imageInfo.Sectors,
                                                   writtenPrefixes, writtenPrefixes     / imageInfo.Sectors);

                        AaruConsole.DebugWriteLine("Aaru Format plugin",
                                                   "{0} ({1:P}% suffixes are correct, {2} ({3:P}%) suffixes have not been dumped, {4} ({5:P}%) suffixes have been written to image",
                                                   correctSuffixes, correctSuffixes     / imageInfo.Sectors,
                                                   notDumpedSuffixes, notDumpedSuffixes / imageInfo.Sectors,
                                                   writtenSuffixes, writtenSuffixes     / imageInfo.Sectors);

                        AaruConsole.DebugWriteLine("Aaru Format plugin",
                                                   "{0} ({1:P}% MODE 2 Form 1 are correct, {2} ({3:P}%) MODE 2 Form 2 are correct, {4} ({5:P}%) MODE 2 Form 2 have empty CRC",
                                                   correctMode2Form1, correctMode2Form1 / imageInfo.Sectors,
                                                   correctMode2Form2, correctMode2Form2 / imageInfo.Sectors,
                                                   emptyMode2Form1, emptyMode2Form1     / imageInfo.Sectors);
                    #endif

                        idxEntry = new IndexEntry
                        {
                            blockType = BlockType.DeDuplicationTable, dataType = DataType.CdSectorPrefixCorrected,
                            offset    = (ulong)imageStream.Position
                        };

                        AaruConsole.DebugWriteLine("Aaru Format plugin",
                                                   "Writing CompactDisc sector prefix DDT to position {0}",
                                                   idxEntry.offset);

                        var ddtHeader = new DdtHeader
                        {
                            identifier  = BlockType.DeDuplicationTable, type = DataType.CdSectorPrefixCorrected,
                            compression = CompressionType.Lzma, entries      = (ulong)sectorPrefixDdt.LongLength,
                            length      = (ulong)(sectorPrefixDdt.LongLength * sizeof(uint))
                        };

                        blockStream = new MemoryStream();
                        var sectorPrefixDdtStream = new MemoryStream();
                        crc64 = new Crc64Context();
                        byte[] ddtEntries = MemoryMarshal.Cast<uint, byte>(sectorPrefixDdt).ToArray();
                        crc64.Update(ddtEntries);
                        sectorPrefixDdtStream.Write(ddtEntries, 0, ddtEntries.Length);

                        byte[] lzmaProperties =
                            CompressDataToStreamWithLZMA(sectorPrefixDdtStream.ToArray(), lzmaEncoderProperties,
                                                         blockStream, false);

                        sectorPrefixDdtStream.Close();
                        ddtHeader.cmpLength = (uint)blockStream.Length + LZMA_PROPERTIES_LENGTH;
                        var cmpCrc64Context = new Crc64Context();
                        cmpCrc64Context.Update(lzmaProperties);
                        cmpCrc64Context.Update(blockStream.ToArray());
                        ddtHeader.cmpCrc64 = BitConverter.ToUInt64(cmpCrc64Context.Final(), 0);

                        structureBytes = new byte[Marshal.SizeOf<DdtHeader>()];
                        MemoryMarshal.Write(structureBytes, ref ddtHeader);
                        imageStream.Write(structureBytes, 0, structureBytes.Length);
                        structureBytes = null;
                        imageStream.Write(lzmaProperties, 0, lzmaProperties.Length);
                        imageStream.Write(blockStream.ToArray(), 0, (int)blockStream.Length);
                        blockStream.Close();
                        blockStream = null;

                        index.RemoveAll(t => t.blockType == BlockType.DeDuplicationTable &&
                                             t.dataType  == DataType.CdSectorPrefixCorrected);

                        index.Add(idxEntry);

                        idxEntry = new IndexEntry
                        {
                            blockType = BlockType.DeDuplicationTable, dataType = DataType.CdSectorSuffixCorrected,
                            offset    = (ulong)imageStream.Position
                        };

                        AaruConsole.DebugWriteLine("Aaru Format plugin",
                                                   "Writing CompactDisc sector suffix DDT to position {0}",
                                                   idxEntry.offset);

                        ddtHeader = new DdtHeader
                        {
                            identifier  = BlockType.DeDuplicationTable, type = DataType.CdSectorSuffixCorrected,
                            compression = CompressionType.Lzma, entries      = (ulong)sectorSuffixDdt.LongLength,
                            length      = (ulong)(sectorSuffixDdt.LongLength * sizeof(uint))
                        };

                        blockStream = new MemoryStream();
                        var sectorSuffixDdtStream = new MemoryStream();
                        crc64      = new Crc64Context();
                        ddtEntries = MemoryMarshal.Cast<uint, byte>(sectorSuffixDdt).ToArray();
                        crc64.Update(ddtEntries);
                        sectorSuffixDdtStream.Write(ddtEntries, 0, ddtEntries.Length);

                        lzmaProperties =
                            CompressDataToStreamWithLZMA(sectorSuffixDdtStream.ToArray(), lzmaEncoderProperties,
                                                         blockStream, false);

                        ddtHeader.cmpLength = (uint)blockStream.Length + LZMA_PROPERTIES_LENGTH;
                        cmpCrc64Context     = new Crc64Context();
                        cmpCrc64Context.Update(lzmaProperties);
                        cmpCrc64Context.Update(blockStream.ToArray());
                        ddtHeader.cmpCrc64 = BitConverter.ToUInt64(cmpCrc64Context.Final(), 0);

                        structureBytes = new byte[Marshal.SizeOf<DdtHeader>()];
                        MemoryMarshal.Write(structureBytes, ref ddtHeader);
                        imageStream.Write(structureBytes, 0, structureBytes.Length);
                        structureBytes = null;
                        imageStream.Write(lzmaProperties, 0, lzmaProperties.Length);
                        imageStream.Write(blockStream.ToArray(), 0, (int)blockStream.Length);
                        blockStream.Close();
                        blockStream = null;

                        index.RemoveAll(t => t.blockType == BlockType.DeDuplicationTable &&
                                             t.dataType  == DataType.CdSectorSuffixCorrected);

                        index.Add(idxEntry);

                        idxEntry = new IndexEntry
                        {
                            blockType = BlockType.DataBlock, dataType = DataType.CdSectorPrefixCorrected,
                            offset    = (ulong)imageStream.Position
                        };

                        AaruConsole.DebugWriteLine("Aaru Format plugin",
                                                   "Writing CD sector corrected prefix block to position {0}",
                                                   idxEntry.offset);

                        Crc64Context.Data(sectorPrefixMs.GetBuffer(), (uint)sectorPrefixMs.Length, out byte[] blockCrc);

                        var prefixBlock = new BlockHeader
                        {
                            identifier = BlockType.DataBlock, type          = DataType.CdSectorPrefixCorrected,
                            length     = (uint)sectorPrefixMs.Length, crc64 = BitConverter.ToUInt64(blockCrc, 0),
                            sectorSize = 16
                        };

                        lzmaProperties = null;

                        if(!compress)
                        {
                            prefixBlock.compression = CompressionType.None;
                            prefixBlock.cmpCrc64    = prefixBlock.crc64;
                            prefixBlock.cmpLength   = prefixBlock.length;
                            blockStream             = sectorPrefixMs;
                        }
                        else
                        {
                            startCompress = DateTime.Now;
                            blockStream   = new MemoryStream();

                            lzmaProperties =
                                CompressDataToStreamWithLZMA(sectorPrefixMs.ToArray(), lzmaEncoderProperties,
                                                             blockStream, false);

                            var cmpCrc = new Crc64Context();
                            cmpCrc.Update(lzmaProperties);
                            cmpCrc.Update(blockStream.ToArray());
                            blockCrc                = cmpCrc.Final();
                            prefixBlock.cmpLength   = (uint)blockStream.Length + LZMA_PROPERTIES_LENGTH;
                            prefixBlock.cmpCrc64    = BitConverter.ToUInt64(blockCrc, 0);
                            prefixBlock.compression = CompressionType.Lzma;

                            endCompress = DateTime.Now;

                            AaruConsole.DebugWriteLine("Aaru Format plugin", "Took {0} seconds to compress prefix",
                                                       (endCompress - startCompress).TotalSeconds);
                        }

                        structureBytes = new byte[Marshal.SizeOf<BlockHeader>()];
                        MemoryMarshal.Write(structureBytes, ref prefixBlock);
                        imageStream.Write(structureBytes, 0, structureBytes.Length);

                        if(prefixBlock.compression == CompressionType.Lzma)
                            imageStream.Write(lzmaProperties, 0, lzmaProperties.Length);

                        imageStream.Write(blockStream.ToArray(), 0, (int)blockStream.Length);
                        blockStream.Close();
                        blockStream = null;

                        index.RemoveAll(t => t.blockType == BlockType.DataBlock &&
                                             t.dataType  == DataType.CdSectorPrefixCorrected);

                        index.Add(idxEntry);

                        idxEntry = new IndexEntry
                        {
                            blockType = BlockType.DataBlock, dataType = DataType.CdSectorSuffixCorrected,
                            offset    = (ulong)imageStream.Position
                        };

                        AaruConsole.DebugWriteLine("Aaru Format plugin",
                                                   "Writing CD sector corrected suffix block to position {0}",
                                                   idxEntry.offset);

                        Crc64Context.Data(sectorSuffixMs.GetBuffer(), (uint)sectorSuffixMs.Length, out blockCrc);

                        var suffixBlock = new BlockHeader
                        {
                            identifier = BlockType.DataBlock, type          = DataType.CdSectorSuffixCorrected,
                            length     = (uint)sectorSuffixMs.Length, crc64 = BitConverter.ToUInt64(blockCrc, 0),
                            sectorSize = 288
                        };

                        lzmaProperties = null;

                        if(!compress)
                        {
                            suffixBlock.compression = CompressionType.None;
                            suffixBlock.cmpCrc64    = suffixBlock.crc64;
                            suffixBlock.cmpLength   = suffixBlock.length;
                            blockStream             = sectorSuffixMs;
                        }
                        else
                        {
                            startCompress = DateTime.Now;
                            blockStream   = new MemoryStream();

                            lzmaProperties =
                                CompressDataToStreamWithLZMA(sectorSuffixMs.ToArray(), lzmaEncoderProperties,
                                                             blockStream, false);

                            var cmpCrc = new Crc64Context();
                            cmpCrc.Update(lzmaProperties);
                            cmpCrc.Update(blockStream.ToArray());
                            blockCrc                = cmpCrc.Final();
                            suffixBlock.cmpLength   = (uint)blockStream.Length + LZMA_PROPERTIES_LENGTH;
                            suffixBlock.cmpCrc64    = BitConverter.ToUInt64(blockCrc, 0);
                            suffixBlock.compression = CompressionType.Lzma;

                            endCompress = DateTime.Now;

                            AaruConsole.DebugWriteLine("Aaru Format plugin", "Took {0} seconds to compress suffix",
                                                       (endCompress - startCompress).TotalSeconds);
                        }

                        structureBytes = new byte[Marshal.SizeOf<BlockHeader>()];
                        MemoryMarshal.Write(structureBytes, ref suffixBlock);
                        imageStream.Write(structureBytes, 0, structureBytes.Length);

                        if(suffixBlock.compression == CompressionType.Lzma)
                            imageStream.Write(lzmaProperties, 0, lzmaProperties.Length);

                        imageStream.Write(blockStream.ToArray(), 0, (int)blockStream.Length);
                        blockStream.Close();
                        blockStream = null;

                        index.RemoveAll(t => t.blockType == BlockType.DataBlock &&
                                             t.dataType  == DataType.CdSectorSuffixCorrected);

                        index.Add(idxEntry);
                    }

                    if(mode2Subheaders != null)
                    {
                        idxEntry = new IndexEntry
                        {
                            blockType = BlockType.DataBlock, dataType = DataType.CompactDiscMode2Subheader,
                            offset    = (ulong)imageStream.Position
                        };

                        AaruConsole.DebugWriteLine("Aaru Format plugin",
                                                   "Writing CD MODE2 subheaders block to position {0}",
                                                   idxEntry.offset);

                        Crc64Context.Data(mode2Subheaders, out byte[] blockCrc);

                        var subheaderBlock = new BlockHeader
                        {
                            identifier = BlockType.DataBlock, type           = DataType.CompactDiscMode2Subheader,
                            length     = (uint)mode2Subheaders.Length, crc64 = BitConverter.ToUInt64(blockCrc, 0),
                            sectorSize = 8
                        };

                        byte[] lzmaProperties = null;

                        if(!compress)
                        {
                            subheaderBlock.compression = CompressionType.None;
                            subheaderBlock.cmpCrc64    = subheaderBlock.crc64;
                            subheaderBlock.cmpLength   = subheaderBlock.length;
                            blockStream                = new MemoryStream(mode2Subheaders);
                        }
                        else
                        {
                            startCompress = DateTime.Now;
                            blockStream   = new MemoryStream();

                            lzmaProperties =
                                CompressDataToStreamWithLZMA(mode2Subheaders, lzmaEncoderProperties, blockStream,
                                                             false);

                            var cmpCrc = new Crc64Context();
                            cmpCrc.Update(lzmaProperties);
                            cmpCrc.Update(blockStream.ToArray());
                            blockCrc                   = cmpCrc.Final();
                            subheaderBlock.cmpLength   = (uint)blockStream.Length + LZMA_PROPERTIES_LENGTH;
                            subheaderBlock.cmpCrc64    = BitConverter.ToUInt64(blockCrc, 0);
                            subheaderBlock.compression = CompressionType.Lzma;

                            endCompress = DateTime.Now;

                            AaruConsole.DebugWriteLine("Aaru Format plugin",
                                                       "Took {0} seconds to compress MODE2 subheaders",
                                                       (endCompress - startCompress).TotalSeconds);
                        }

                        structureBytes = new byte[Marshal.SizeOf<BlockHeader>()];
                        MemoryMarshal.Write(structureBytes, ref subheaderBlock);
                        imageStream.Write(structureBytes, 0, structureBytes.Length);

                        if(subheaderBlock.compression == CompressionType.Lzma)
                            imageStream.Write(lzmaProperties, 0, lzmaProperties.Length);

                        imageStream.Write(blockStream.ToArray(), 0, (int)blockStream.Length);

                        index.RemoveAll(t => t.blockType == BlockType.DataBlock &&
                                             t.dataType  == DataType.CompactDiscMode2Subheader);

                        index.Add(idxEntry);
                        blockStream.Close();
                        blockStream = null;
                    }

                    if(sectorSubchannel != null)
                    {
                        idxEntry = new IndexEntry
                        {
                            blockType = BlockType.DataBlock, dataType = DataType.CdSectorSubchannel,
                            offset    = (ulong)imageStream.Position
                        };

                        AaruConsole.DebugWriteLine("Aaru Format plugin", "Writing CD subchannel block to position {0}",
                                                   idxEntry.offset);

                        Crc64Context.Data(sectorSubchannel, out byte[] blockCrc);

                        var subchannelBlock = new BlockHeader
                        {
                            identifier = BlockType.DataBlock, type            = DataType.CdSectorSubchannel,
                            length     = (uint)sectorSubchannel.Length, crc64 = BitConverter.ToUInt64(blockCrc, 0),
                            sectorSize = 96
                        };

                        byte[] lzmaProperties = null;

                        if(!compress)
                        {
                            subchannelBlock.compression = CompressionType.None;
                            subchannelBlock.cmpCrc64    = subchannelBlock.crc64;
                            subchannelBlock.cmpLength   = subchannelBlock.length;
                            blockStream                 = new MemoryStream(sectorSubchannel);
                        }
                        else
                        {
                            startCompress = DateTime.Now;
                            byte[] transformedSubchannel = ClauniaSubchannelTransform(sectorSubchannel);
                            blockStream = new MemoryStream();

                            lzmaProperties =
                                CompressDataToStreamWithLZMA(transformedSubchannel, lzmaEncoderProperties, blockStream,
                                                             false);

                            var cmpCrc = new Crc64Context();
                            cmpCrc.Update(lzmaProperties);
                            cmpCrc.Update(blockStream.ToArray());
                            blockCrc                    = cmpCrc.Final();
                            subchannelBlock.cmpLength   = (uint)blockStream.Length + LZMA_PROPERTIES_LENGTH;
                            subchannelBlock.cmpCrc64    = BitConverter.ToUInt64(blockCrc, 0);
                            subchannelBlock.compression = CompressionType.LzmaClauniaSubchannelTransform;

                            endCompress = DateTime.Now;

                            AaruConsole.DebugWriteLine("Aaru Format plugin", "Took {0} seconds to compress subchannel",
                                                       (endCompress - startCompress).TotalSeconds);
                        }

                        structureBytes = new byte[Marshal.SizeOf<BlockHeader>()];
                        MemoryMarshal.Write(structureBytes, ref subchannelBlock);
                        imageStream.Write(structureBytes, 0, structureBytes.Length);

                        if(subchannelBlock.compression == CompressionType.Lzma ||
                           subchannelBlock.compression == CompressionType.LzmaClauniaSubchannelTransform)
                            imageStream.Write(lzmaProperties, 0, lzmaProperties.Length);

                        imageStream.Write(blockStream.ToArray(), 0, (int)blockStream.Length);

                        index.RemoveAll(t => t.blockType == BlockType.DataBlock &&
                                             t.dataType  == DataType.CdSectorSubchannel);

                        index.Add(idxEntry);
                        blockStream.Close();
                        blockStream = null;
                    }

                    List<TrackEntry> trackEntries = new List<TrackEntry>();

                    foreach(Track track in Tracks)
                    {
                        trackFlags.TryGetValue((byte)track.TrackSequence, out byte flags);
                        trackIsrcs.TryGetValue((byte)track.TrackSequence, out string isrc);

                        if((flags & (int)CdFlags.DataTrack) == 0 &&
                           track.TrackType                  != TrackType.Audio)
                            flags += (byte)CdFlags.DataTrack;

                        trackEntries.Add(new TrackEntry
                        {
                            sequence = (byte)track.TrackSequence, type   = track.TrackType,
                            start    = (long)track.TrackStartSector, end = (long)track.TrackEndSector,
                            pregap   = (long)track.TrackPregap, session  = (byte)track.TrackSession, isrc = isrc,
                            flags    = flags
                        });
                    }

                    // If there are tracks build the tracks block
                    if(trackEntries.Count > 0)
                    {
                        blockStream = new MemoryStream();

                        foreach(TrackEntry entry in trackEntries)
                        {
                            structurePointer =
                                System.Runtime.InteropServices.Marshal.AllocHGlobal(Marshal.SizeOf<TrackEntry>());

                            structureBytes = new byte[Marshal.SizeOf<TrackEntry>()];
                            System.Runtime.InteropServices.Marshal.StructureToPtr(entry, structurePointer, true);

                            System.Runtime.InteropServices.Marshal.Copy(structurePointer, structureBytes, 0,
                                                                        structureBytes.Length);

                            System.Runtime.InteropServices.Marshal.FreeHGlobal(structurePointer);
                            blockStream.Write(structureBytes, 0, structureBytes.Length);
                        }

                        Crc64Context.Data(blockStream.ToArray(), out byte[] trksCrc);

                        var trkHeader = new TracksHeader
                        {
                            identifier = BlockType.TracksBlock, entries = (ushort)trackEntries.Count,
                            crc64      = BitConverter.ToUInt64(trksCrc, 0)
                        };

                        AaruConsole.DebugWriteLine("Aaru Format plugin", "Writing tracks to position {0}",
                                                   imageStream.Position);

                        index.RemoveAll(t => t.blockType == BlockType.TracksBlock && t.dataType == DataType.NoData);

                        index.Add(new IndexEntry
                        {
                            blockType = BlockType.TracksBlock, dataType = DataType.NoData,
                            offset    = (ulong)imageStream.Position
                        });

                        structureBytes = new byte[Marshal.SizeOf<TracksHeader>()];
                        MemoryMarshal.Write(structureBytes, ref trkHeader);
                        imageStream.Write(structureBytes, 0, structureBytes.Length);
                        imageStream.Write(blockStream.ToArray(), 0, (int)blockStream.Length);
                        blockStream.Close();
                        blockStream = null;
                    }

                    break;
                case XmlMediaType.BlockMedia:
                    if(sectorSubchannel != null &&
                       (imageInfo.MediaType == MediaType.AppleFileWare ||
                        imageInfo.MediaType == MediaType.AppleSonySS   ||
                        imageInfo.MediaType == MediaType.AppleSonyDS   ||
                        imageInfo.MediaType == MediaType.AppleProfile  ||
                        imageInfo.MediaType == MediaType.AppleWidget   ||
                        imageInfo.MediaType == MediaType.PriamDataTower))
                    {
                        DataType tagType = DataType.NoData;

                        switch(imageInfo.MediaType)
                        {
                            case MediaType.AppleSonySS:
                            case MediaType.AppleSonyDS:
                                tagType = DataType.AppleSonyTag;

                                break;
                            case MediaType.AppleFileWare:
                            case MediaType.AppleProfile:
                            case MediaType.AppleWidget:
                                tagType = DataType.AppleProfileTag;

                                break;
                            case MediaType.PriamDataTower:
                                tagType = DataType.PriamDataTowerTag;

                                break;
                        }

                        idxEntry = new IndexEntry
                        {
                            blockType = BlockType.DataBlock, dataType = tagType, offset = (ulong)imageStream.Position
                        };

                        AaruConsole.DebugWriteLine("Aaru Format plugin",
                                                   "Writing apple sector tag block to position {0}", idxEntry.offset);

                        Crc64Context.Data(sectorSubchannel, out byte[] blockCrc);

                        var subchannelBlock = new BlockHeader
                        {
                            identifier = BlockType.DataBlock, type = tagType, length = (uint)sectorSubchannel.Length,
                            crc64      = BitConverter.ToUInt64(blockCrc, 0)
                        };

                        switch(imageInfo.MediaType)
                        {
                            case MediaType.AppleSonySS:
                            case MediaType.AppleSonyDS:
                                subchannelBlock.sectorSize = 12;

                                break;
                            case MediaType.AppleFileWare:
                            case MediaType.AppleProfile:
                            case MediaType.AppleWidget:
                                subchannelBlock.sectorSize = 20;

                                break;
                            case MediaType.PriamDataTower:
                                subchannelBlock.sectorSize = 24;

                                break;
                        }

                        byte[] lzmaProperties = null;

                        if(!compress)
                        {
                            subchannelBlock.compression = CompressionType.None;
                            subchannelBlock.cmpCrc64    = subchannelBlock.crc64;
                            subchannelBlock.cmpLength   = subchannelBlock.length;
                            blockStream                 = new MemoryStream(sectorSubchannel);
                        }
                        else
                        {
                            blockStream = new MemoryStream();

                            lzmaProperties =
                                CompressDataToStreamWithLZMA(sectorSubchannel, lzmaEncoderProperties, blockStream,
                                                             false);

                            var cmpCrc = new Crc64Context();
                            cmpCrc.Update(lzmaProperties);
                            cmpCrc.Update(blockStream.ToArray());
                            blockCrc                    = cmpCrc.Final();
                            subchannelBlock.cmpLength   = (uint)blockStream.Length + LZMA_PROPERTIES_LENGTH;
                            subchannelBlock.cmpCrc64    = BitConverter.ToUInt64(blockCrc, 0);
                            subchannelBlock.compression = CompressionType.Lzma;
                        }

                        structureBytes = new byte[Marshal.SizeOf<BlockHeader>()];
                        MemoryMarshal.Write(structureBytes, ref subchannelBlock);
                        imageStream.Write(structureBytes, 0, structureBytes.Length);

                        if(subchannelBlock.compression == CompressionType.Lzma)
                            imageStream.Write(lzmaProperties, 0, lzmaProperties.Length);

                        imageStream.Write(blockStream.ToArray(), 0, (int)blockStream.Length);

                        index.RemoveAll(t => t.blockType == BlockType.DataBlock && t.dataType == tagType);

                        index.Add(idxEntry);
                        blockStream.Close();
                        blockStream = null;
                    }

                    break;
            }

            // Write metadata if present
            SetMetadataFromTags();
            var metadataBlock = new MetadataBlock();
            blockStream = new MemoryStream();
            blockStream.Write(new byte[Marshal.SizeOf<MetadataBlock>()], 0, Marshal.SizeOf<MetadataBlock>());
            byte[] tmpUtf16Le;

            if(imageInfo.MediaSequence     > 0 &&
               imageInfo.LastMediaSequence > 0)
            {
                metadataBlock.identifier        = BlockType.MetadataBlock;
                metadataBlock.mediaSequence     = imageInfo.MediaSequence;
                metadataBlock.lastMediaSequence = imageInfo.LastMediaSequence;
            }

            if(!string.IsNullOrWhiteSpace(imageInfo.Creator))
            {
                tmpUtf16Le                  = Encoding.Unicode.GetBytes(imageInfo.Creator);
                metadataBlock.identifier    = BlockType.MetadataBlock;
                metadataBlock.creatorOffset = (uint)blockStream.Position;
                metadataBlock.creatorLength = (uint)(tmpUtf16Le.Length + 2);
                blockStream.Write(tmpUtf16Le, 0, tmpUtf16Le.Length);

                blockStream.Write(new byte[]
                {
                    0, 0
                }, 0, 2);
            }

            if(!string.IsNullOrWhiteSpace(imageInfo.Comments))
            {
                tmpUtf16Le                   = Encoding.Unicode.GetBytes(imageInfo.Comments);
                metadataBlock.identifier     = BlockType.MetadataBlock;
                metadataBlock.commentsOffset = (uint)blockStream.Position;
                metadataBlock.commentsLength = (uint)(tmpUtf16Le.Length + 2);
                blockStream.Write(tmpUtf16Le, 0, tmpUtf16Le.Length);

                blockStream.Write(new byte[]
                {
                    0, 0
                }, 0, 2);
            }

            if(!string.IsNullOrWhiteSpace(imageInfo.MediaTitle))
            {
                tmpUtf16Le                     = Encoding.Unicode.GetBytes(imageInfo.MediaTitle);
                metadataBlock.identifier       = BlockType.MetadataBlock;
                metadataBlock.mediaTitleOffset = (uint)blockStream.Position;
                metadataBlock.mediaTitleLength = (uint)(tmpUtf16Le.Length + 2);
                blockStream.Write(tmpUtf16Le, 0, tmpUtf16Le.Length);

                blockStream.Write(new byte[]
                {
                    0, 0
                }, 0, 2);
            }

            if(!string.IsNullOrWhiteSpace(imageInfo.MediaManufacturer))
            {
                tmpUtf16Le                            = Encoding.Unicode.GetBytes(imageInfo.MediaManufacturer);
                metadataBlock.identifier              = BlockType.MetadataBlock;
                metadataBlock.mediaManufacturerOffset = (uint)blockStream.Position;
                metadataBlock.mediaManufacturerLength = (uint)(tmpUtf16Le.Length + 2);
                blockStream.Write(tmpUtf16Le, 0, tmpUtf16Le.Length);

                blockStream.Write(new byte[]
                {
                    0, 0
                }, 0, 2);
            }

            if(!string.IsNullOrWhiteSpace(imageInfo.MediaModel))
            {
                tmpUtf16Le                     = Encoding.Unicode.GetBytes(imageInfo.MediaModel);
                metadataBlock.identifier       = BlockType.MetadataBlock;
                metadataBlock.mediaModelOffset = (uint)blockStream.Position;
                metadataBlock.mediaModelLength = (uint)(tmpUtf16Le.Length + 2);
                blockStream.Write(tmpUtf16Le, 0, tmpUtf16Le.Length);

                blockStream.Write(new byte[]
                {
                    0, 0
                }, 0, 2);
            }

            if(!string.IsNullOrWhiteSpace(imageInfo.MediaSerialNumber))
            {
                tmpUtf16Le                            = Encoding.Unicode.GetBytes(imageInfo.MediaSerialNumber);
                metadataBlock.identifier              = BlockType.MetadataBlock;
                metadataBlock.mediaSerialNumberOffset = (uint)blockStream.Position;
                metadataBlock.mediaSerialNumberLength = (uint)(tmpUtf16Le.Length + 2);
                blockStream.Write(tmpUtf16Le, 0, tmpUtf16Le.Length);

                blockStream.Write(new byte[]
                {
                    0, 0
                }, 0, 2);
            }

            if(!string.IsNullOrWhiteSpace(imageInfo.MediaBarcode))
            {
                tmpUtf16Le                       = Encoding.Unicode.GetBytes(imageInfo.MediaBarcode);
                metadataBlock.identifier         = BlockType.MetadataBlock;
                metadataBlock.mediaBarcodeOffset = (uint)blockStream.Position;
                metadataBlock.mediaBarcodeLength = (uint)(tmpUtf16Le.Length + 2);
                blockStream.Write(tmpUtf16Le, 0, tmpUtf16Le.Length);

                blockStream.Write(new byte[]
                {
                    0, 0
                }, 0, 2);
            }

            if(!string.IsNullOrWhiteSpace(imageInfo.MediaPartNumber))
            {
                tmpUtf16Le                          = Encoding.Unicode.GetBytes(imageInfo.MediaPartNumber);
                metadataBlock.identifier            = BlockType.MetadataBlock;
                metadataBlock.mediaPartNumberOffset = (uint)blockStream.Position;
                metadataBlock.mediaPartNumberLength = (uint)(tmpUtf16Le.Length + 2);
                blockStream.Write(tmpUtf16Le, 0, tmpUtf16Le.Length);

                blockStream.Write(new byte[]
                {
                    0, 0
                }, 0, 2);
            }

            if(!string.IsNullOrWhiteSpace(imageInfo.DriveManufacturer))
            {
                tmpUtf16Le                            = Encoding.Unicode.GetBytes(imageInfo.DriveManufacturer);
                metadataBlock.identifier              = BlockType.MetadataBlock;
                metadataBlock.driveManufacturerOffset = (uint)blockStream.Position;
                metadataBlock.driveManufacturerLength = (uint)(tmpUtf16Le.Length + 2);
                blockStream.Write(tmpUtf16Le, 0, tmpUtf16Le.Length);

                blockStream.Write(new byte[]
                {
                    0, 0
                }, 0, 2);
            }

            if(!string.IsNullOrWhiteSpace(imageInfo.DriveModel))
            {
                tmpUtf16Le                     = Encoding.Unicode.GetBytes(imageInfo.DriveModel);
                metadataBlock.identifier       = BlockType.MetadataBlock;
                metadataBlock.driveModelOffset = (uint)blockStream.Position;
                metadataBlock.driveModelLength = (uint)(tmpUtf16Le.Length + 2);
                blockStream.Write(tmpUtf16Le, 0, tmpUtf16Le.Length);

                blockStream.Write(new byte[]
                {
                    0, 0
                }, 0, 2);
            }

            if(!string.IsNullOrWhiteSpace(imageInfo.DriveSerialNumber))
            {
                tmpUtf16Le                            = Encoding.Unicode.GetBytes(imageInfo.DriveSerialNumber);
                metadataBlock.identifier              = BlockType.MetadataBlock;
                metadataBlock.driveSerialNumberOffset = (uint)blockStream.Position;
                metadataBlock.driveSerialNumberLength = (uint)(tmpUtf16Le.Length + 2);
                blockStream.Write(tmpUtf16Le, 0, tmpUtf16Le.Length);

                blockStream.Write(new byte[]
                {
                    0, 0
                }, 0, 2);
            }

            if(!string.IsNullOrWhiteSpace(imageInfo.DriveFirmwareRevision))
            {
                tmpUtf16Le                                = Encoding.Unicode.GetBytes(imageInfo.DriveFirmwareRevision);
                metadataBlock.identifier                  = BlockType.MetadataBlock;
                metadataBlock.driveFirmwareRevisionOffset = (uint)blockStream.Position;
                metadataBlock.driveFirmwareRevisionLength = (uint)(tmpUtf16Le.Length + 2);
                blockStream.Write(tmpUtf16Le, 0, tmpUtf16Le.Length);

                blockStream.Write(new byte[]
                {
                    0, 0
                }, 0, 2);
            }

            // Check if we set up any metadata earlier, then write its block
            if(metadataBlock.identifier == BlockType.MetadataBlock)
            {
                AaruConsole.DebugWriteLine("Aaru Format plugin", "Writing metadata to position {0}",
                                           imageStream.Position);

                metadataBlock.blockSize = (uint)blockStream.Length;
                structureBytes          = new byte[Marshal.SizeOf<MetadataBlock>()];
                MemoryMarshal.Write(structureBytes, ref metadataBlock);
                blockStream.Position = 0;
                blockStream.Write(structureBytes, 0, structureBytes.Length);
                index.RemoveAll(t => t.blockType == BlockType.MetadataBlock && t.dataType == DataType.NoData);

                index.Add(new IndexEntry
                {
                    blockType = BlockType.MetadataBlock, dataType = DataType.NoData,
                    offset    = (ulong)imageStream.Position
                });

                imageStream.Write(blockStream.ToArray(), 0, (int)blockStream.Length);
                blockStream.Close();
                blockStream = null;
            }

            header.indexOffset = (ulong)imageStream.Position;

            AaruConsole.DebugWriteLine("Aaru Format plugin", "Writing index to position {0}", header.indexOffset);

            blockStream = new MemoryStream();

            // Write index to memory
            foreach(IndexEntry entry in index)
            {
                structureBytes = new byte[Marshal.SizeOf<IndexEntry>()];
                IndexEntry indexEntry = entry;
                MemoryMarshal.Write(structureBytes, ref indexEntry);
                blockStream.Write(structureBytes, 0, structureBytes.Length);
            }

            Crc64Context.Data(blockStream.ToArray(), out byte[] idxCrc);

            var idxHeader = new IndexHeader
            {
                identifier = BlockType.Index, entries = (ushort)index.Count, crc64 = BitConverter.ToUInt64(idxCrc, 0)
            };

            // Write index to disk
            structureBytes = new byte[Marshal.SizeOf<IndexHeader>()];
            MemoryMarshal.Write(structureBytes, ref idxHeader);
            imageStream.Write(structureBytes, 0, structureBytes.Length);
            imageStream.Write(blockStream.ToArray(), 0, (int)blockStream.Length);
            blockStream.Close();
            blockStream = null;

            AaruConsole.DebugWriteLine("Aaru Format plugin", "Writing header");
            header.lastWrittenTime = DateTime.UtcNow.ToFileTimeUtc();
            imageStream.Position   = 0;
            structurePointer       = System.Runtime.InteropServices.Marshal.AllocHGlobal(Marshal.SizeOf<AaruHeader>());
            structureBytes         = new byte[Marshal.SizeOf<AaruHeader>()];
            System.Runtime.InteropServices.Marshal.StructureToPtr(header, structurePointer, true);
            System.Runtime.InteropServices.Marshal.Copy(structurePointer, structureBytes, 0, structureBytes.Length);
            System.Runtime.InteropServices.Marshal.FreeHGlobal(structurePointer);
            imageStream.Write(structureBytes, 0, structureBytes.Length);

            imageStream.Flush();
            imageStream.Close();
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, true, true);

            IsWriting    = false;
            ErrorMessage = "";

            return true;
        }

        public bool SetMetadata(ImageInfo metadata)
        {
            imageInfo.Creator               = metadata.Creator;
            imageInfo.Comments              = metadata.Comments;
            imageInfo.MediaManufacturer     = metadata.MediaManufacturer;
            imageInfo.MediaModel            = metadata.MediaModel;
            imageInfo.MediaSerialNumber     = metadata.MediaSerialNumber;
            imageInfo.MediaBarcode          = metadata.MediaBarcode;
            imageInfo.MediaPartNumber       = metadata.MediaPartNumber;
            imageInfo.MediaSequence         = metadata.MediaSequence;
            imageInfo.LastMediaSequence     = metadata.LastMediaSequence;
            imageInfo.DriveManufacturer     = metadata.DriveManufacturer;
            imageInfo.DriveModel            = metadata.DriveModel;
            imageInfo.DriveSerialNumber     = metadata.DriveSerialNumber;
            imageInfo.DriveFirmwareRevision = metadata.DriveFirmwareRevision;
            imageInfo.MediaTitle            = metadata.MediaTitle;

            return true;
        }

        public bool SetGeometry(uint cylinders, uint heads, uint sectorsPerTrack)
        {
            if(!IsWriting)
            {
                ErrorMessage = "Tried to write on a non-writable image";

                return false;
            }

            if(imageInfo.XmlMediaType != XmlMediaType.BlockMedia)
            {
                ErrorMessage = "Tried to set geometry on a media that doesn't suppport it";

                return false;
            }

            geometryBlock = new GeometryBlock
            {
                identifier      = BlockType.GeometryBlock, cylinders = cylinders, heads = heads,
                sectorsPerTrack = sectorsPerTrack
            };

            ErrorMessage = "";

            return true;
        }

        public bool WriteSectorTag(byte[] data, ulong sectorAddress, SectorTagType tag)
        {
            if(!IsWriting)
            {
                ErrorMessage = "Tried to write on a non-writable image";

                return false;
            }

            if(sectorAddress >= imageInfo.Sectors)
            {
                ErrorMessage = "Tried to write past image size";

                return false;
            }

            var track = new Track();

            switch(tag)
            {
                case SectorTagType.CdTrackFlags:
                case SectorTagType.CdTrackIsrc:
                case SectorTagType.CdSectorSubchannel:
                    if(imageInfo.XmlMediaType != XmlMediaType.OpticalDisc)
                    {
                        ErrorMessage = "Incorrect tag for disk type";

                        return false;
                    }

                    track = Tracks.FirstOrDefault(trk => sectorAddress >= trk.TrackStartSector &&
                                                         sectorAddress <= trk.TrackEndSector);

                    if(track.TrackSequence    == 0 &&
                       track.TrackStartSector == 0 &&
                       track.TrackEndSector   == 0)
                    {
                        ErrorMessage = $"Can't found track containing {sectorAddress}";

                        return false;
                    }

                    break;
            }

            switch(tag)
            {
                case SectorTagType.CdTrackFlags:
                {
                    if(data.Length != 1)
                    {
                        ErrorMessage = "Incorrect data size for track flags";

                        return false;
                    }

                    trackFlags.Add((byte)track.TrackSequence, data[0]);

                    return true;
                }

                case SectorTagType.CdTrackIsrc:
                {
                    if(data != null)
                        trackIsrcs.Add((byte)track.TrackSequence, Encoding.UTF8.GetString(data));

                    return true;
                }

                case SectorTagType.CdSectorSubchannel:
                {
                    if(data.Length != 96)
                    {
                        ErrorMessage = "Incorrect data size for subchannel";

                        return false;
                    }

                    if(sectorSubchannel == null)
                        sectorSubchannel = new byte[imageInfo.Sectors * 96];

                    Array.Copy(data, 0, sectorSubchannel, (int)(96 * sectorAddress), 96);

                    return true;
                }

                default:
                    ErrorMessage = $"Don't know how to write sector tag type {tag}";

                    return false;
            }
        }

        public bool WriteSectorsTag(byte[] data, ulong sectorAddress, uint length, SectorTagType tag)
        {
            if(!IsWriting)
            {
                ErrorMessage = "Tried to write on a non-writable image";

                return false;
            }

            if(sectorAddress + length > imageInfo.Sectors)
            {
                ErrorMessage = "Tried to write past image size";

                return false;
            }

            switch(tag)
            {
                case SectorTagType.CdTrackFlags:
                case SectorTagType.CdTrackIsrc: return WriteSectorTag(data, sectorAddress, tag);
                case SectorTagType.CdSectorSubchannel:
                {
                    if(data.Length % 96 != 0)
                    {
                        ErrorMessage = "Incorrect data size for subchannel";

                        return false;
                    }

                    if(sectorSubchannel == null)
                        sectorSubchannel = new byte[imageInfo.Sectors * 96];

                    if((sectorAddress * 96) + (length * 96) > (ulong)sectorSubchannel.LongLength)
                    {
                        ErrorMessage = "Tried to write more data than possible";

                        return false;
                    }

                    Array.Copy(data, 0, sectorSubchannel, (int)(96 * sectorAddress), 96 * length);

                    return true;
                }

                default:
                    ErrorMessage = $"Don't know how to write sector tag type {tag}";

                    return false;
            }
        }

        public bool SetDumpHardware(List<DumpHardwareType> dumpHardware)
        {
            DumpHardware = dumpHardware;

            return true;
        }

        public bool SetCicmMetadata(CICMMetadataType metadata)
        {
            CicmMetadata = metadata;

            return true;
        }

        /// <summary>This method exists to ensure the .NET memory allocator frees the LZ tree on each call</summary>
        /// <param name="data">Data to compress</param>
        /// <param name="properties">LZMA properties</param>
        /// <param name="stream">Stream where to write the compressed data to</param>
        /// <returns>The properties as a byte array</returns>
        static byte[] CompressDataToStreamWithLZMA(byte[] data, LzmaEncoderProperties properties, Stream stream,
                                                   bool foo)
        {
            byte[] propertiesArray;

            using(var lzmaStream = new LzmaStream(properties, false, stream))
            {
                lzmaStream.Write(data, 0, data.Length);
                propertiesArray = new byte[lzmaStream.Properties.Length];
                lzmaStream.Properties.CopyTo(propertiesArray, 0);
            }

            return propertiesArray;
        }
    }
}