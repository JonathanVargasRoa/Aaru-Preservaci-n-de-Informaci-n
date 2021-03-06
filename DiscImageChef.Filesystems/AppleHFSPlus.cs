// /***************************************************************************
// The Disc Image Chef
// ----------------------------------------------------------------------------
//
// Filename       : AppleHFSPlus.cs
// Author(s)      : Natalia Portillo <claunia@claunia.com>
//
// Component      : Apple Hierarchical File System Plus plugin.
//
// --[ Description ] ----------------------------------------------------------
//
//     Identifies the Apple Hierarchical File System Plus and shows information.
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
using System.Runtime.InteropServices;
using System.Text;
using DiscImageChef.CommonTypes;
using DiscImageChef.DiscImages;
using Schemas;

namespace DiscImageChef.Filesystems
{
    // Information from Apple TechNote 1150: https://developer.apple.com/legacy/library/technotes/tn/tn1150.html
    public class AppleHFSPlus : IFilesystem
    {
        /// <summary>
        ///     "BD", HFS magic
        /// </summary>
        const ushort HFS_MAGIC = 0x4244;
        /// <summary>
        ///     "H+", HFS+ magic
        /// </summary>
        const ushort HFSP_MAGIC = 0x482B;
        /// <summary>
        ///     "HX", HFSX magic
        /// </summary>
        const ushort HFSX_MAGIC = 0x4858;

        public FileSystemType XmlFsType { get; private set; }
        public Encoding Encoding { get; private set; }
        public string Name => "Apple HFS+ filesystem";
        public Guid Id => new Guid("36405F8D-0D26-6EBE-436F-62F0586B4F08");

        public bool Identify(IMediaImage imagePlugin, Partition partition)
        {
            if(2 + partition.Start >= partition.End) return false;

            ushort drSigWord;

            ulong hfspOffset;

            uint sectorsToRead = 0x800 / imagePlugin.Info.SectorSize;
            if(0x800 % imagePlugin.Info.SectorSize > 0) sectorsToRead++;

            byte[] vhSector = imagePlugin.ReadSectors(partition.Start, sectorsToRead);

            drSigWord = BigEndianBitConverter.ToUInt16(vhSector, 0x400); // Check for HFS Wrapper MDB

            if(drSigWord == HFS_MAGIC) // "BD"
            {
                drSigWord = BigEndianBitConverter.ToUInt16(vhSector, 0x47C); // Read embedded HFS+ signature

                if(drSigWord == HFSP_MAGIC) // "H+"
                {
                    ushort xdrStABNt = BigEndianBitConverter.ToUInt16(vhSector, 0x47E);

                    uint drAlBlkSiz = BigEndianBitConverter.ToUInt32(vhSector, 0x414);

                    ushort drAlBlSt = BigEndianBitConverter.ToUInt16(vhSector, 0x41C);

                    hfspOffset = (ulong)((drAlBlSt * 512 + xdrStABNt * drAlBlkSiz) / imagePlugin.Info.SectorSize);
                }
                else hfspOffset = 0;
            }
            else hfspOffset = 0;

            vhSector = imagePlugin.ReadSectors(partition.Start + hfspOffset, sectorsToRead); // Read volume header

            drSigWord = BigEndianBitConverter.ToUInt16(vhSector, 0x400);
            return drSigWord == HFSP_MAGIC || drSigWord == HFSX_MAGIC;
        }

        public void GetInformation(IMediaImage imagePlugin, Partition partition, out string information,
                                   Encoding encoding)
        {
            Encoding = Encoding.BigEndianUnicode;
            information = "";

            ushort drSigWord;
            HFSPlusVolumeHeader HPVH = new HFSPlusVolumeHeader();

            ulong hfspOffset;
            bool wrapped;

            uint sectorsToRead = 0x800 / imagePlugin.Info.SectorSize;
            if(0x800 % imagePlugin.Info.SectorSize > 0) sectorsToRead++;

            byte[] vhSector = imagePlugin.ReadSectors(partition.Start, sectorsToRead);

            drSigWord = BigEndianBitConverter.ToUInt16(vhSector, 0x400); // Check for HFS Wrapper MDB

            if(drSigWord == HFS_MAGIC) // "BD"
            {
                drSigWord = BigEndianBitConverter.ToUInt16(vhSector, 0x47C); // Read embedded HFS+ signature

                if(drSigWord == HFSP_MAGIC) // "H+"
                {
                    ushort xdrStABNt = BigEndianBitConverter.ToUInt16(vhSector, 0x47E);

                    uint drAlBlkSiz = BigEndianBitConverter.ToUInt32(vhSector, 0x414);

                    ushort drAlBlSt = BigEndianBitConverter.ToUInt16(vhSector, 0x41C);

                    hfspOffset = (ulong)((drAlBlSt * 512 + xdrStABNt * drAlBlkSiz) / imagePlugin.Info.SectorSize);
                    wrapped = true;
                }
                else
                {
                    hfspOffset = 0;
                    wrapped = false;
                }
            }
            else
            {
                hfspOffset = 0;
                wrapped = false;
            }

            vhSector = imagePlugin.ReadSectors(partition.Start + hfspOffset, sectorsToRead); // Read volume header

            HPVH.signature = BigEndianBitConverter.ToUInt16(vhSector, 0x400);
            if(HPVH.signature == HFSP_MAGIC || HPVH.signature == HFSX_MAGIC)
            {
                StringBuilder sb = new StringBuilder();

                if(HPVH.signature == 0x482B) sb.AppendLine("HFS+ filesystem.");
                if(HPVH.signature == 0x4858) sb.AppendLine("HFSX filesystem.");
                if(wrapped) sb.AppendLine("Volume is wrapped inside an HFS volume.");

                byte[] tmp = new byte[0x400];
                Array.Copy(vhSector, 0x400, tmp, 0, 0x400);
                vhSector = tmp;

                HPVH = BigEndianMarshal.ByteArrayToStructureBigEndian<HFSPlusVolumeHeader>(vhSector);

                if(HPVH.version == 4 || HPVH.version == 5)
                {
                    sb.AppendFormat("Filesystem version is {0}.", HPVH.version).AppendLine();

                    if((HPVH.attributes & 0x80) == 0x80) sb.AppendLine("Volume is locked on hardware.");
                    if((HPVH.attributes & 0x100) == 0x100) sb.AppendLine("Volume is unmounted.");
                    if((HPVH.attributes & 0x200) == 0x200) sb.AppendLine("There are bad blocks in the extents file.");
                    if((HPVH.attributes & 0x400) == 0x400) sb.AppendLine("Volume does not require cache.");
                    if((HPVH.attributes & 0x800) == 0x800) sb.AppendLine("Volume state is inconsistent.");
                    if((HPVH.attributes & 0x1000) == 0x1000) sb.AppendLine("CNIDs are reused.");
                    if((HPVH.attributes & 0x2000) == 0x2000) sb.AppendLine("Volume is journaled.");
                    if((HPVH.attributes & 0x8000) == 0x8000) sb.AppendLine("Volume is locked on software.");

                    sb.AppendFormat("Implementation that last mounted the volume: \"{0}\".",
                                    Encoding.ASCII.GetString(HPVH.lastMountedVersion)).AppendLine();
                    if((HPVH.attributes & 0x2000) == 0x2000)
                        sb.AppendFormat("Journal starts at allocation block {0}.", HPVH.journalInfoBlock).AppendLine();
                    sb.AppendFormat("Creation date: {0}", DateHandlers.MacToDateTime(HPVH.createDate)).AppendLine();
                    sb.AppendFormat("Last modification date: {0}", DateHandlers.MacToDateTime(HPVH.modifyDate))
                      .AppendLine();
                    if(HPVH.backupDate > 0)
                        sb.AppendFormat("Last backup date: {0}", DateHandlers.MacToDateTime(HPVH.backupDate))
                          .AppendLine();
                    else sb.AppendLine("Volume has never been backed up");
                    if(HPVH.backupDate > 0)
                        sb.AppendFormat("Last check date: {0}", DateHandlers.MacToDateTime(HPVH.checkedDate))
                          .AppendLine();
                    else sb.AppendLine("Volume has never been checked up");
                    sb.AppendFormat("{0} files on volume.", HPVH.fileCount).AppendLine();
                    sb.AppendFormat("{0} folders on volume.", HPVH.folderCount).AppendLine();
                    sb.AppendFormat("{0} bytes per allocation block.", HPVH.blockSize).AppendLine();
                    sb.AppendFormat("{0} allocation blocks.", HPVH.totalBlocks).AppendLine();
                    sb.AppendFormat("{0} free blocks.", HPVH.freeBlocks).AppendLine();
                    sb.AppendFormat("Next allocation block: {0}.", HPVH.nextAllocation).AppendLine();
                    sb.AppendFormat("Resource fork clump size: {0} bytes.", HPVH.rsrcClumpSize).AppendLine();
                    sb.AppendFormat("Data fork clump size: {0} bytes.", HPVH.dataClumpSize).AppendLine();
                    sb.AppendFormat("Next unused CNID: {0}.", HPVH.nextCatalogID).AppendLine();
                    sb.AppendFormat("Volume has been mounted writable {0} times.", HPVH.writeCount).AppendLine();
                    sb.AppendFormat("Allocation File is {0} bytes.", HPVH.allocationFile_logicalSize).AppendLine();
                    sb.AppendFormat("Extents File is {0} bytes.", HPVH.extentsFile_logicalSize).AppendLine();
                    sb.AppendFormat("Catalog File is {0} bytes.", HPVH.catalogFile_logicalSize).AppendLine();
                    sb.AppendFormat("Attributes File is {0} bytes.", HPVH.attributesFile_logicalSize).AppendLine();
                    sb.AppendFormat("Startup File is {0} bytes.", HPVH.startupFile_logicalSize).AppendLine();
                    sb.AppendLine("Finder info:");
                    sb.AppendFormat("CNID of bootable system's directory: {0}", HPVH.drFndrInfo0).AppendLine();
                    sb.AppendFormat("CNID of first-run application's directory: {0}", HPVH.drFndrInfo1).AppendLine();
                    sb.AppendFormat("CNID of previously opened directory: {0}", HPVH.drFndrInfo2).AppendLine();
                    sb.AppendFormat("CNID of bootable Mac OS 8 or 9 directory: {0}", HPVH.drFndrInfo3).AppendLine();
                    sb.AppendFormat("CNID of bootable Mac OS X directory: {0}", HPVH.drFndrInfo5).AppendLine();
                    if(HPVH.drFndrInfo6 != 0 && HPVH.drFndrInfo7 != 0)
                        sb.AppendFormat("Mac OS X Volume ID: {0:X8}{1:X8}", HPVH.drFndrInfo6, HPVH.drFndrInfo7)
                          .AppendLine();

                    XmlFsType = new FileSystemType();
                    if(HPVH.backupDate > 0)
                    {
                        XmlFsType.BackupDate = DateHandlers.MacToDateTime(HPVH.backupDate);
                        XmlFsType.BackupDateSpecified = true;
                    }
                    XmlFsType.Bootable |= HPVH.drFndrInfo0 != 0 || HPVH.drFndrInfo3 != 0 || HPVH.drFndrInfo5 != 0;
                    XmlFsType.Clusters = HPVH.totalBlocks;
                    XmlFsType.ClusterSize = (int)HPVH.blockSize;
                    if(HPVH.createDate > 0)
                    {
                        XmlFsType.CreationDate = DateHandlers.MacToDateTime(HPVH.createDate);
                        XmlFsType.CreationDateSpecified = true;
                    }
                    XmlFsType.Dirty = (HPVH.attributes & 0x100) != 0x100;
                    XmlFsType.Files = HPVH.fileCount;
                    XmlFsType.FilesSpecified = true;
                    XmlFsType.FreeClusters = HPVH.freeBlocks;
                    XmlFsType.FreeClustersSpecified = true;
                    if(HPVH.modifyDate > 0)
                    {
                        XmlFsType.ModificationDate = DateHandlers.MacToDateTime(HPVH.modifyDate);
                        XmlFsType.ModificationDateSpecified = true;
                    }
                    if(HPVH.signature == 0x482B) XmlFsType.Type = "HFS+";
                    if(HPVH.signature == 0x4858) XmlFsType.Type = "HFSX";
                    if(HPVH.drFndrInfo6 != 0 && HPVH.drFndrInfo7 != 0)
                        XmlFsType.VolumeSerial = $"{HPVH.drFndrInfo6:X8}{HPVH.drFndrInfo7:X8}";
                    XmlFsType.SystemIdentifier = Encoding.ASCII.GetString(HPVH.lastMountedVersion);
                }
                else
                {
                    sb.AppendFormat("Filesystem version is {0}.", HPVH.version).AppendLine();
                    sb.AppendLine("This version is not supported yet.");
                }

                information = sb.ToString();
            }
            else return;
        }

        /// <summary>
        ///     HFS+ Volume Header, should be at offset 0x0400 bytes in volume with a size of 532 bytes
        /// </summary>
        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        struct HFSPlusVolumeHeader
        {
            /// <summary>0x000, "H+" for HFS+, "HX" for HFSX</summary>
            public ushort signature;
            /// <summary>0x002, 4 for HFS+, 5 for HFSX</summary>
            public ushort version;
            /// <summary>0x004, Volume attributes</summary>
            public uint attributes;
            /// <summary>
            ///     0x008, Implementation that last mounted the volume.
            ///     Reserved by Apple:
            ///     "8.10" Mac OS 8.1 to 9.2.2
            ///     "10.0" Mac OS X
            ///     "HFSJ" Journaled implementation
            ///     "fsck" /sbin/fsck
            /// </summary>
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)] public byte[] lastMountedVersion;
            /// <summary>0x00C, Allocation block number containing the journal</summary>
            public uint journalInfoBlock;
            /// <summary>0x010, Date of volume creation</summary>
            public uint createDate;
            /// <summary>0x014, Date of last volume modification</summary>
            public uint modifyDate;
            /// <summary>0x018, Date of last backup</summary>
            public uint backupDate;
            /// <summary>0x01C, Date of last consistency check</summary>
            public uint checkedDate;
            /// <summary>0x020, File on the volume</summary>
            public uint fileCount;
            /// <summary>0x024, Folders on the volume</summary>
            public uint folderCount;
            /// <summary>0x028, Bytes per allocation block</summary>
            public uint blockSize;
            /// <summary>0x02C, Allocation blocks on the volume</summary>
            public uint totalBlocks;
            /// <summary>0x030, Free allocation blocks</summary>
            public uint freeBlocks;
            /// <summary>0x034, Hint for next allocation block</summary>
            public uint nextAllocation;
            /// <summary>0x038, Resource fork clump size</summary>
            public uint rsrcClumpSize;
            /// <summary>0x03C, Data fork clump size</summary>
            public uint dataClumpSize;
            /// <summary>0x040, Next unused CNID</summary>
            public uint nextCatalogID;
            /// <summary>0x044, Times that the volume has been mounted writable</summary>
            public uint writeCount;
            /// <summary>0x048, Used text encoding hints</summary>
            public ulong encodingsBitmap;
            /// <summary>0x050, finderInfo[0], CNID for bootable system's directory</summary>
            public uint drFndrInfo0;
            /// <summary>0x054, finderInfo[1], CNID of the directory containing the boot application</summary>
            public uint drFndrInfo1;
            /// <summary>0x058, finderInfo[2], CNID of the directory that should be opened on boot</summary>
            public uint drFndrInfo2;
            /// <summary>0x05C, finderInfo[3], CNID for Mac OS 8 or 9 directory</summary>
            public uint drFndrInfo3;
            /// <summary>0x060, finderInfo[4], Reserved</summary>
            public uint drFndrInfo4;
            /// <summary>0x064, finderInfo[5], CNID for Mac OS X directory</summary>
            public uint drFndrInfo5;
            /// <summary>0x068, finderInfo[6], first part of Mac OS X volume ID</summary>
            public uint drFndrInfo6;
            /// <summary>0x06C, finderInfo[7], second part of Mac OS X volume ID</summary>
            public uint drFndrInfo7;
            // HFSPlusForkData     allocationFile;
            /// <summary>0x070</summary>
            public ulong allocationFile_logicalSize;
            /// <summary>0x078</summary>
            public uint allocationFile_clumpSize;
            /// <summary>0x07C</summary>
            public uint allocationFile_totalBlocks;
            /// <summary>0x080</summary>
            public uint allocationFile_extents_startBlock0;
            /// <summary>0x084</summary>
            public uint allocationFile_extents_blockCount0;
            /// <summary>0x088</summary>
            public uint allocationFile_extents_startBlock1;
            /// <summary>0x08C</summary>
            public uint allocationFile_extents_blockCount1;
            /// <summary>0x090</summary>
            public uint allocationFile_extents_startBlock2;
            /// <summary>0x094</summary>
            public uint allocationFile_extents_blockCount2;
            /// <summary>0x098</summary>
            public uint allocationFile_extents_startBlock3;
            /// <summary>0x09C</summary>
            public uint allocationFile_extents_blockCount3;
            /// <summary>0x0A0</summary>
            public uint allocationFile_extents_startBlock4;
            /// <summary>0x0A4</summary>
            public uint allocationFile_extents_blockCount4;
            /// <summary>0x0A8</summary>
            public uint allocationFile_extents_startBlock5;
            /// <summary>0x0AC</summary>
            public uint allocationFile_extents_blockCount5;
            /// <summary>0x0B0</summary>
            public uint allocationFile_extents_startBlock6;
            /// <summary>0x0B4</summary>
            public uint allocationFile_extents_blockCount6;
            /// <summary>0x0B8</summary>
            public uint allocationFile_extents_startBlock7;
            /// <summary>0x0BC</summary>
            public uint allocationFile_extents_blockCount7;
            // HFSPlusForkData     extentsFile;
            /// <summary>0x0C0</summary>
            public ulong extentsFile_logicalSize;
            /// <summary>0x0C8</summary>
            public uint extentsFile_clumpSize;
            /// <summary>0x0CC</summary>
            public uint extentsFile_totalBlocks;
            /// <summary>0x0D0</summary>
            public uint extentsFile_extents_startBlock0;
            /// <summary>0x0D4</summary>
            public uint extentsFile_extents_blockCount0;
            /// <summary>0x0D8</summary>
            public uint extentsFile_extents_startBlock1;
            /// <summary>0x0DC</summary>
            public uint extentsFile_extents_blockCount1;
            /// <summary>0x0E0</summary>
            public uint extentsFile_extents_startBlock2;
            /// <summary>0x0E4</summary>
            public uint extentsFile_extents_blockCount2;
            /// <summary>0x0E8</summary>
            public uint extentsFile_extents_startBlock3;
            /// <summary>0x0EC</summary>
            public uint extentsFile_extents_blockCount3;
            /// <summary>0x0F0</summary>
            public uint extentsFile_extents_startBlock4;
            /// <summary>0x0F4</summary>
            public uint extentsFile_extents_blockCount4;
            /// <summary>0x0F8</summary>
            public uint extentsFile_extents_startBlock5;
            /// <summary>0x0FC</summary>
            public uint extentsFile_extents_blockCount5;
            /// <summary>0x100</summary>
            public uint extentsFile_extents_startBlock6;
            /// <summary>0x104</summary>
            public uint extentsFile_extents_blockCount6;
            /// <summary>0x108</summary>
            public uint extentsFile_extents_startBlock7;
            /// <summary>0x10C</summary>
            public uint extentsFile_extents_blockCount7;
            // HFSPlusForkData     catalogFile;
            /// <summary>0x110</summary>
            public ulong catalogFile_logicalSize;
            /// <summary>0x118</summary>
            public uint catalogFile_clumpSize;
            /// <summary>0x11C</summary>
            public uint catalogFile_totalBlocks;
            /// <summary>0x120</summary>
            public uint catalogFile_extents_startBlock0;
            /// <summary>0x124</summary>
            public uint catalogFile_extents_blockCount0;
            /// <summary>0x128</summary>
            public uint catalogFile_extents_startBlock1;
            /// <summary>0x12C</summary>
            public uint catalogFile_extents_blockCount1;
            /// <summary>0x130</summary>
            public uint catalogFile_extents_startBlock2;
            /// <summary>0x134</summary>
            public uint catalogFile_extents_blockCount2;
            /// <summary>0x138</summary>
            public uint catalogFile_extents_startBlock3;
            /// <summary>0x13C</summary>
            public uint catalogFile_extents_blockCount3;
            /// <summary>0x140</summary>
            public uint catalogFile_extents_startBlock4;
            /// <summary>0x144</summary>
            public uint catalogFile_extents_blockCount4;
            /// <summary>0x148</summary>
            public uint catalogFile_extents_startBlock5;
            /// <summary>0x14C</summary>
            public uint catalogFile_extents_blockCount5;
            /// <summary>0x150</summary>
            public uint catalogFile_extents_startBlock6;
            /// <summary>0x154</summary>
            public uint catalogFile_extents_blockCount6;
            /// <summary>0x158</summary>
            public uint catalogFile_extents_startBlock7;
            /// <summary>0x15C</summary>
            public uint catalogFile_extents_blockCount7;
            // HFSPlusForkData     attributesFile;
            /// <summary>0x160</summary>
            public ulong attributesFile_logicalSize;
            /// <summary>0x168</summary>
            public uint attributesFile_clumpSize;
            /// <summary>0x16C</summary>
            public uint attributesFile_totalBlocks;
            /// <summary>0x170</summary>
            public uint attributesFile_extents_startBlock0;
            /// <summary>0x174</summary>
            public uint attributesFile_extents_blockCount0;
            /// <summary>0x178</summary>
            public uint attributesFile_extents_startBlock1;
            /// <summary>0x17C</summary>
            public uint attributesFile_extents_blockCount1;
            /// <summary>0x180</summary>
            public uint attributesFile_extents_startBlock2;
            /// <summary>0x184</summary>
            public uint attributesFile_extents_blockCount2;
            /// <summary>0x188</summary>
            public uint attributesFile_extents_startBlock3;
            /// <summary>0x18C</summary>
            public uint attributesFile_extents_blockCount3;
            /// <summary>0x190</summary>
            public uint attributesFile_extents_startBlock4;
            /// <summary>0x194</summary>
            public uint attributesFile_extents_blockCount4;
            /// <summary>0x198</summary>
            public uint attributesFile_extents_startBlock5;
            /// <summary>0x19C</summary>
            public uint attributesFile_extents_blockCount5;
            /// <summary>0x1A0</summary>
            public uint attributesFile_extents_startBlock6;
            /// <summary>0x1A4</summary>
            public uint attributesFile_extents_blockCount6;
            /// <summary>0x1A8</summary>
            public uint attributesFile_extents_startBlock7;
            /// <summary>0x1AC</summary>
            public uint attributesFile_extents_blockCount7;
            // HFSPlusForkData     startupFile;
            /// <summary>0x1B0</summary>
            public ulong startupFile_logicalSize;
            /// <summary>0x1B8</summary>
            public uint startupFile_clumpSize;
            /// <summary>0x1BC</summary>
            public uint startupFile_totalBlocks;
            /// <summary>0x1C0</summary>
            public uint startupFile_extents_startBlock0;
            /// <summary>0x1C4</summary>
            public uint startupFile_extents_blockCount0;
            /// <summary>0x1C8</summary>
            public uint startupFile_extents_startBlock1;
            /// <summary>0x1D0</summary>
            public uint startupFile_extents_blockCount1;
            /// <summary>0x1D4</summary>
            public uint startupFile_extents_startBlock2;
            /// <summary>0x1D8</summary>
            public uint startupFile_extents_blockCount2;
            /// <summary>0x1DC</summary>
            public uint startupFile_extents_startBlock3;
            /// <summary>0x1E0</summary>
            public uint startupFile_extents_blockCount3;
            /// <summary>0x1E4</summary>
            public uint startupFile_extents_startBlock4;
            /// <summary>0x1E8</summary>
            public uint startupFile_extents_blockCount4;
            /// <summary>0x1EC</summary>
            public uint startupFile_extents_startBlock5;
            /// <summary>0x1F0</summary>
            public uint startupFile_extents_blockCount5;
            /// <summary>0x1F4</summary>
            public uint startupFile_extents_startBlock6;
            /// <summary>0x1F8</summary>
            public uint startupFile_extents_blockCount6;
            /// <summary>0x1FC</summary>
            public uint startupFile_extents_startBlock7;
            /// <summary>0x200</summary>
            public uint startupFile_extents_blockCount7;
        }
    }
}