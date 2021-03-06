// /***************************************************************************
// The Disc Image Chef
// ----------------------------------------------------------------------------
//
// Filename       : Locus.cs
// Author(s)      : Natalia Portillo <claunia@claunia.com>
//
// Component      : Locus filesystem plugin
//
// --[ Description ] ----------------------------------------------------------
//
//     Identifies the Locus filesystem and shows information.
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
//     License aint with this library; if not, see <http://www.gnu.org/licenses/>.
//
// ----------------------------------------------------------------------------
// Copyright © 2011-2018 Natalia Portillo
// ****************************************************************************/

using System;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Text;
using DiscImageChef.CommonTypes;
using DiscImageChef.Console;
using DiscImageChef.DiscImages;
using Schemas;
// Commit count
using commitcnt_t = System.Int32;
// Disk address
using daddr_t = System.Int32;
// Fstore
using fstore_t = System.Int32;
// Global File System number
using gfs_t = System.Int32;
// Inode number
using ino_t = System.Int32;
// Filesystem pack number
using pckno_t = System.Int16;
// Timestamp
using time_t = System.Int32;

namespace DiscImageChef.Filesystems
{
    public class Locus : IFilesystem
    {
        const int NICINOD = 325;
        const int NICFREE = 600;
        const int OLDNICINOD = 700;
        const int OLDNICFREE = 500;

        const uint Locus_Magic = 0xFFEEDDCD;
        const uint Locus_Cigam = 0xCDDDEEFF;
        const uint Locus_OldMagic = 0xFFEEDDCC;
        const uint Locus_OldCigam = 0xCCDDEEFF;

        public FileSystemType XmlFsType { get; private set; }
        public Encoding Encoding { get; private set; }
        public string Name => "Locus Filesystem Plugin";
        public Guid Id => new Guid("1A70B30A-437D-479A-88E1-D0C9C1797FF4");

        public bool Identify(IMediaImage imagePlugin, Partition partition)
        {
            if(imagePlugin.Info.SectorSize < 512) return false;

            for(ulong location = 0; location <= 8; location++)
            {
                Locus_Superblock LocusSb = new Locus_Superblock();

                uint sbSize = (uint)(Marshal.SizeOf(LocusSb) / imagePlugin.Info.SectorSize);
                if(Marshal.SizeOf(LocusSb) % imagePlugin.Info.SectorSize != 0) sbSize++;

                if(partition.Start + location + sbSize >= imagePlugin.Info.Sectors) break;

                byte[] sector = imagePlugin.ReadSectors(partition.Start + location, sbSize);
                if(sector.Length < Marshal.SizeOf(LocusSb)) return false;

                IntPtr sbPtr = Marshal.AllocHGlobal(Marshal.SizeOf(LocusSb));
                Marshal.Copy(sector, 0, sbPtr, Marshal.SizeOf(LocusSb));
                LocusSb = (Locus_Superblock)Marshal.PtrToStructure(sbPtr, typeof(Locus_Superblock));
                Marshal.FreeHGlobal(sbPtr);

                DicConsole.DebugWriteLine("Locus plugin", "magic at {1} = 0x{0:X8}", LocusSb.s_magic, location);

                if(LocusSb.s_magic == Locus_Magic || LocusSb.s_magic == Locus_Cigam ||
                   LocusSb.s_magic == Locus_OldMagic || LocusSb.s_magic == Locus_OldCigam) return true;
            }

            return false;
        }

        public void GetInformation(IMediaImage imagePlugin, Partition partition, out string information,
                                   Encoding encoding)
        {
            Encoding = encoding ?? Encoding.GetEncoding("iso-8859-15");
            information = "";
            if(imagePlugin.Info.SectorSize < 512) return;

            Locus_Superblock LocusSb = new Locus_Superblock();
            byte[] sector = null;

            for(ulong location = 0; location <= 8; location++)
            {
                uint sbSize = (uint)(Marshal.SizeOf(LocusSb) / imagePlugin.Info.SectorSize);
                if(Marshal.SizeOf(LocusSb) % imagePlugin.Info.SectorSize != 0) sbSize++;

                sector = imagePlugin.ReadSectors(partition.Start + location, sbSize);
                if(sector.Length < Marshal.SizeOf(LocusSb)) return;

                IntPtr sbPtr = Marshal.AllocHGlobal(Marshal.SizeOf(LocusSb));
                Marshal.Copy(sector, 0, sbPtr, Marshal.SizeOf(LocusSb));
                LocusSb = (Locus_Superblock)Marshal.PtrToStructure(sbPtr, typeof(Locus_Superblock));
                Marshal.FreeHGlobal(sbPtr);

                if(LocusSb.s_magic == Locus_Magic || LocusSb.s_magic == Locus_Cigam ||
                   LocusSb.s_magic == Locus_OldMagic || LocusSb.s_magic == Locus_OldCigam) break;
            }

            // We don't care about old version for information
            if(LocusSb.s_magic != Locus_Magic && LocusSb.s_magic != Locus_Cigam && LocusSb.s_magic != Locus_OldMagic &&
               LocusSb.s_magic != Locus_OldCigam) return;

            // Numerical arrays are not important for information so no need to swap them
            if(LocusSb.s_magic == Locus_Cigam || LocusSb.s_magic == Locus_OldCigam)
            {
                LocusSb = BigEndianMarshal.ByteArrayToStructureBigEndian<Locus_Superblock>(sector);
                LocusSb.s_flags = (LocusFlags)Swapping.Swap((ushort)LocusSb.s_flags);
            }

            StringBuilder sb = new StringBuilder();

            sb.AppendLine(LocusSb.s_magic == Locus_OldMagic ? "Locus filesystem (old)" : "Locus filesystem");

            int blockSize = LocusSb.s_version == LocusVersion.SB_SB4096 ? 4096 : 1024;

            string s_fsmnt = StringHandlers.CToString(LocusSb.s_fsmnt, Encoding);
            string s_fpack = StringHandlers.CToString(LocusSb.s_fpack, Encoding);

            DicConsole.DebugWriteLine("Locus plugin", "LocusSb.s_magic = 0x{0:X8}", LocusSb.s_magic);
            DicConsole.DebugWriteLine("Locus plugin", "LocusSb.s_gfs = {0}", LocusSb.s_gfs);
            DicConsole.DebugWriteLine("Locus plugin", "LocusSb.s_fsize = {0}", LocusSb.s_fsize);
            DicConsole.DebugWriteLine("Locus plugin", "LocusSb.s_lwm = {0}", LocusSb.s_lwm);
            DicConsole.DebugWriteLine("Locus plugin", "LocusSb.s_hwm = {0}", LocusSb.s_hwm);
            DicConsole.DebugWriteLine("Locus plugin", "LocusSb.s_llst = {0}", LocusSb.s_llst);
            DicConsole.DebugWriteLine("Locus plugin", "LocusSb.s_fstore = {0}", LocusSb.s_fstore);
            DicConsole.DebugWriteLine("Locus plugin", "LocusSb.s_time = {0}", LocusSb.s_time);
            DicConsole.DebugWriteLine("Locus plugin", "LocusSb.s_tfree = {0}", LocusSb.s_tfree);
            DicConsole.DebugWriteLine("Locus plugin", "LocusSb.s_isize = {0}", LocusSb.s_isize);
            DicConsole.DebugWriteLine("Locus plugin", "LocusSb.s_nfree = {0}", LocusSb.s_nfree);
            DicConsole.DebugWriteLine("Locus plugin", "LocusSb.s_flags = {0}", LocusSb.s_flags);
            DicConsole.DebugWriteLine("Locus plugin", "LocusSb.s_tinode = {0}", LocusSb.s_tinode);
            DicConsole.DebugWriteLine("Locus plugin", "LocusSb.s_lasti = {0}", LocusSb.s_lasti);
            DicConsole.DebugWriteLine("Locus plugin", "LocusSb.s_nbehind = {0}", LocusSb.s_nbehind);
            DicConsole.DebugWriteLine("Locus plugin", "LocusSb.s_gfspack = {0}", LocusSb.s_gfspack);
            DicConsole.DebugWriteLine("Locus plugin", "LocusSb.s_ninode = {0}", LocusSb.s_ninode);
            DicConsole.DebugWriteLine("Locus plugin", "LocusSb.s_flock = {0}", LocusSb.s_flock);
            DicConsole.DebugWriteLine("Locus plugin", "LocusSb.s_ilock = {0}", LocusSb.s_ilock);
            DicConsole.DebugWriteLine("Locus plugin", "LocusSb.s_fmod = {0}", LocusSb.s_fmod);
            DicConsole.DebugWriteLine("Locus plugin", "LocusSb.s_version = {0}", LocusSb.s_version);

            sb.AppendFormat("Superblock last modified on {0}", DateHandlers.UnixToDateTime(LocusSb.s_time))
              .AppendLine();
            sb.AppendFormat("Volume has {0} blocks of {1} bytes each (total {2} bytes)", LocusSb.s_fsize, blockSize,
                            LocusSb.s_fsize * blockSize).AppendLine();
            sb.AppendFormat("{0} blocks free ({1} bytes)", LocusSb.s_tfree, LocusSb.s_tfree * blockSize).AppendLine();
            sb.AppendFormat("I-node list uses {0} blocks", LocusSb.s_isize).AppendLine();
            sb.AppendFormat("{0} free inodes", LocusSb.s_tinode).AppendLine();
            sb.AppendFormat("Next free inode search will start at inode {0}", LocusSb.s_lasti).AppendLine();
            sb.AppendFormat("There are an estimate of {0} free inodes before next search start", LocusSb.s_nbehind)
              .AppendLine();
            if(LocusSb.s_flags.HasFlag(LocusFlags.SB_RDONLY)) sb.AppendLine("Read-only volume");
            if(LocusSb.s_flags.HasFlag(LocusFlags.SB_CLEAN)) sb.AppendLine("Clean volume");
            if(LocusSb.s_flags.HasFlag(LocusFlags.SB_DIRTY)) sb.AppendLine("Dirty volume");
            if(LocusSb.s_flags.HasFlag(LocusFlags.SB_RMV)) sb.AppendLine("Removable volume");
            if(LocusSb.s_flags.HasFlag(LocusFlags.SB_PRIMPACK)) sb.AppendLine("This is the primary pack");
            if(LocusSb.s_flags.HasFlag(LocusFlags.SB_REPLTYPE)) sb.AppendLine("Replicated volume");
            if(LocusSb.s_flags.HasFlag(LocusFlags.SB_USER)) sb.AppendLine("User replicated volume");
            if(LocusSb.s_flags.HasFlag(LocusFlags.SB_BACKBONE)) sb.AppendLine("Backbone volume");
            if(LocusSb.s_flags.HasFlag(LocusFlags.SB_NFS)) sb.AppendLine("NFS volume");
            if(LocusSb.s_flags.HasFlag(LocusFlags.SB_BYHAND)) sb.AppendLine("Volume inhibits automatic fsck");
            if(LocusSb.s_flags.HasFlag(LocusFlags.SB_NOSUID)) sb.AppendLine("Set-uid/set-gid is disabled");
            if(LocusSb.s_flags.HasFlag(LocusFlags.SB_SYNCW)) sb.AppendLine("Volume uses synchronous writes");
            sb.AppendFormat("Volume label: {0}", s_fsmnt).AppendLine();
            sb.AppendFormat("Physical volume name: {0}", s_fpack).AppendLine();
            sb.AppendFormat("Global File System number: {0}", LocusSb.s_gfs).AppendLine();
            sb.AppendFormat("Global File System pack number {0}", LocusSb.s_gfspack).AppendLine();

            information = sb.ToString();

            XmlFsType = new FileSystemType
            {
                Type = "Locus filesystem",
                ClusterSize = blockSize,
                Clusters = LocusSb.s_fsize,
                // Sometimes it uses one, or the other. Use the bigger
                VolumeName = string.IsNullOrEmpty(s_fsmnt) ? s_fpack : s_fsmnt,
                ModificationDate = DateHandlers.UnixToDateTime(LocusSb.s_time),
                ModificationDateSpecified = true,
                Dirty = !LocusSb.s_flags.HasFlag(LocusFlags.SB_CLEAN) || LocusSb.s_flags.HasFlag(LocusFlags.SB_DIRTY),
                FreeClusters = LocusSb.s_tfree,
                FreeClustersSpecified = true
            };
        }

        [SuppressMessage("ReSharper", "InconsistentNaming")]
        [SuppressMessage("ReSharper", "BuiltInTypeReferenceStyle")]
        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        struct Locus_Superblock
        {
            public uint s_magic; /* identifies this as a locus filesystem */
            /* defined as a constant below */
            public gfs_t s_gfs; /* global filesystem number */
            public daddr_t s_fsize; /* size in blocks of entire volume */
            /* several ints for replicated filsystems */
            public commitcnt_t s_lwm; /* all prior commits propagated */
            public commitcnt_t s_hwm; /* highest commit propagated */
            /* oldest committed version in the list.
             * llst mod NCMTLST is the offset of commit #llst in the list,
             * which wraps around from there.
             */
            public commitcnt_t s_llst;
            public fstore_t s_fstore; /* filesystem storage bit mask; if the
                   filsys is replicated and this is not a
                   primary or backbone copy, this bit mask
                   determines which files are stored */

            public time_t s_time; /* last super block update */
            public daddr_t s_tfree; /* total free blocks*/

            public ino_t s_isize; /* size in blocks of i-list */
            public short s_nfree; /* number of addresses in s_free */
            public LocusFlags s_flags; /* filsys flags, defined below */
            public ino_t s_tinode; /* total free inodes */
            public ino_t s_lasti; /* start place for circular search */
            public ino_t s_nbehind; /* est # free inodes before s_lasti */
            public pckno_t s_gfspack; /* global filesystem pack number */
            public short s_ninode; /* number of i-nodes in s_inode */
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)] public short[] s_dinfo; /* interleave stuff */
            //#define s_m s_dinfo[0]
            //#define s_skip  s_dinfo[0]      /* AIX defines  */
            //#define s_n s_dinfo[1]
            //#define s_cyl   s_dinfo[1]      /* AIX defines  */
            public byte s_flock; /* lock during free list manipulation */
            public byte s_ilock; /* lock during i-list manipulation */
            public byte s_fmod; /* super block modified flag */
            public LocusVersion s_version; /* version of the data format in fs. */
            /*  defined below. */
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public byte[] s_fsmnt; /* name of this file system */
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)]
            public byte[] s_fpack; /* name of this physical volume */
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = NICINOD)] public ino_t[] s_inode; /* free i-node list */
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = NICFREE)]
            public daddr_t[] su_free; /* free block list for non-replicated filsys */
            public byte s_byteorder; /* byte order of integers */
        }

        [SuppressMessage("ReSharper", "InconsistentNaming")]
        [SuppressMessage("ReSharper", "BuiltInTypeReferenceStyle")]
        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        struct Locus_OldSuperblock
        {
            public uint s_magic; /* identifies this as a locus filesystem */
            /* defined as a constant below */
            public gfs_t s_gfs; /* global filesystem number */
            public daddr_t s_fsize; /* size in blocks of entire volume */
            /* several ints for replicated filsystems */
            public commitcnt_t s_lwm; /* all prior commits propagated */
            public commitcnt_t s_hwm; /* highest commit propagated */
            /* oldest committed version in the list.
             * llst mod NCMTLST is the offset of commit #llst in the list,
             * which wraps around from there.
             */
            public commitcnt_t s_llst;
            public fstore_t s_fstore; /* filesystem storage bit mask; if the
                   filsys is replicated and this is not a
                   primary or backbone copy, this bit mask
                   determines which files are stored */

            public time_t s_time; /* last super block update */
            public daddr_t s_tfree; /* total free blocks*/

            public ino_t s_isize; /* size in blocks of i-list */
            public short s_nfree; /* number of addresses in s_free */
            public LocusFlags s_flags; /* filsys flags, defined below */
            public ino_t s_tinode; /* total free inodes */
            public ino_t s_lasti; /* start place for circular search */
            public ino_t s_nbehind; /* est # free inodes before s_lasti */
            public pckno_t s_gfspack; /* global filesystem pack number */
            public short s_ninode; /* number of i-nodes in s_inode */
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)] public short[] s_dinfo; /* interleave stuff */
            //#define s_m s_dinfo[0]
            //#define s_skip  s_dinfo[0]      /* AIX defines  */
            //#define s_n s_dinfo[1]
            //#define s_cyl   s_dinfo[1]      /* AIX defines  */
            public byte s_flock; /* lock during free list manipulation */
            public byte s_ilock; /* lock during i-list manipulation */
            public byte s_fmod; /* super block modified flag */
            public LocusVersion s_version; /* version of the data format in fs. */
            /*  defined below. */
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public byte[] s_fsmnt; /* name of this file system */
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)]
            public byte[] s_fpack; /* name of this physical volume */
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = OLDNICINOD)] public ino_t[] s_inode; /* free i-node list */
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = OLDNICFREE)]
            public daddr_t[] su_free; /* free block list for non-replicated filsys */
            public byte s_byteorder; /* byte order of integers */
        }

        [SuppressMessage("ReSharper", "InconsistentNaming")]
        [SuppressMessage("ReSharper", "BuiltInTypeReferenceStyle")]
        [Flags]
        enum LocusFlags : ushort
        {
            SB_RDONLY = 0x1, /* no writes on filesystem */
            SB_CLEAN = 0x2, /* fs unmounted cleanly (or checks run) */
            SB_DIRTY = 0x4, /* fs mounted without CLEAN bit set */
            SB_RMV = 0x8, /* fs is a removable file system */
            SB_PRIMPACK = 0x10, /* This is the primary pack of the filesystem */
            SB_REPLTYPE = 0x20, /* This is a replicated type filesystem. */
            SB_USER = 0x40, /* This is a "user" replicated filesystem. */
            SB_BACKBONE = 0x80, /* backbone pack ; complete copy of primary pack but not modifiable */
            SB_NFS = 0x100, /* This is a NFS type filesystem */
            SB_BYHAND = 0x200, /* Inhibits automatic fscks on a mangled file system */
            SB_NOSUID = 0x400, /* Set-uid/Set-gid is disabled */
            SB_SYNCW = 0x800 /* Synchronous Write */
        }

        [SuppressMessage("ReSharper", "InconsistentNaming")]
        [SuppressMessage("ReSharper", "BuiltInTypeReferenceStyle")]
        [Flags]
        enum LocusVersion : byte
        {
            SB_SB4096 = 1, /* smallblock filesys with 4096 byte blocks */
            SB_B1024 = 2, /* 1024 byte block filesystem */
            NUMSCANDEV = 5 /* Used by scangfs(), refed in space.h */
        }
    }
}