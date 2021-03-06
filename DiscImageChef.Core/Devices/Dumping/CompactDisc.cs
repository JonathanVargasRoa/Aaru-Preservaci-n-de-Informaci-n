// /***************************************************************************
// The Disc Image Chef
// ----------------------------------------------------------------------------
//
// Filename       : CompactDisc.cs
// Author(s)      : Natalia Portillo <claunia@claunia.com>
//
// Component      : Core algorithms.
//
// --[ Description ] ----------------------------------------------------------
//
//     Dumps CDs and DDCDs.
//
// --[ License ] --------------------------------------------------------------
//
//     This program is free software: you can redistribute it and/or modify
//     it under the terms of the GNU General Public License as
//     published by the Free Software Foundation, either version 3 of the
//     License, or (at your option) any later version.
//
//     This program is distributed in the hope that it will be useful,
//     but WITHOUT ANY WARRANTY; without even the implied warranty of
//     MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
//     GNU General Public License for more details.
//
//     You should have received a copy of the GNU General Public License
//     along with this program.  If not, see <http://www.gnu.org/licenses/>.
//
// ----------------------------------------------------------------------------
// Copyright © 2011-2018 Natalia Portillo
// ****************************************************************************/

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml.Serialization;
using DiscImageChef.Console;
using DiscImageChef.Core.Logging;
using DiscImageChef.Decoders.CD;
using DiscImageChef.Decoders.SCSI;
using DiscImageChef.Decoders.SCSI.MMC;
using DiscImageChef.Devices;
using DiscImageChef.DiscImages;
using DiscImageChef.Filters;
using DiscImageChef.Metadata;
using Extents;
using Schemas;
using MediaType = DiscImageChef.CommonTypes.MediaType;
using PlatformID = DiscImageChef.Interop.PlatformID;
using Session = DiscImageChef.Decoders.CD.Session;
using TrackType = DiscImageChef.DiscImages.TrackType;

namespace DiscImageChef.Core.Devices.Dumping
{
    /// <summary>
    ///     Implement dumping Compact Discs
    /// </summary>
    // TODO: Barcode and pregaps
    static class CompactDisc
    {
        /// <summary>
        ///     Dumps a compact disc
        /// </summary>
        /// <param name="dev">Device</param>
        /// <param name="devicePath">Path to the device</param>
        /// <param name="outputPrefix">Prefix for output data files</param>
        /// <param name="outputPlugin">Plugin for output file</param>
        /// <param name="retryPasses">How many times to retry</param>
        /// <param name="force">Force to continue dump whenever possible</param>
        /// <param name="dumpRaw">Dump scrambled sectors</param>
        /// <param name="persistent">Store whatever data the drive returned on error</param>
        /// <param name="stopOnError">Stop dump on first error</param>
        /// <param name="resume">Information for dump resuming</param>
        /// <param name="dumpLog">Dump logger</param>
        /// <param name="dskType">Disc type as detected in MMC layer</param>
        /// <param name="dumpLeadIn">Try to read and dump as much Lead-in as possible</param>
        /// <param name="outputPath">Path to output file</param>
        /// <param name="formatOptions">Formats to pass to output file plugin</param>
        /// <param name="encoding">Encoding to use when analyzing dump</param>
        /// <exception cref="NotImplementedException">If trying to dump scrambled sectors</exception>
        /// <exception cref="InvalidOperationException">If the resume file is invalid</exception>
        /// <exception cref="ArgumentOutOfRangeException">If the track type is unknown (never)</exception>
        internal static void Dump(Device                     dev,          string      devicePath,
                                  IWritableImage             outputPlugin, ushort      retryPasses,
                                  bool                       force,        bool        dumpRaw,
                                  bool                       persistent,   bool        stopOnError, ref MediaType dskType,
                                  ref Resume                 resume,       ref DumpLog dumpLog,
                                  bool                       dumpLeadIn,   Encoding    encoding,
                                  string                     outputPrefix, string      outputPath,
                                  Dictionary<string, string> formatOptions,
                                  CICMMetadataType           preSidecar, uint skip,
                                  bool                       nometadata, bool notrim)
        {
            uint               subSize;
            DateTime           start;
            DateTime           end;
            bool               readcd;
            bool               sense         = false;
            const uint         SECTOR_SIZE   = 2352;
            FullTOC.CDFullTOC? toc           = null;
            double             totalDuration = 0;
            double             currentSpeed  = 0;
            double             maxSpeed      = double.MinValue;
            double             minSpeed      = double.MaxValue;
            uint               blocksToRead  = 64;
            bool               aborted       = false;
            System.Console.CancelKeyPress += (sender, e) => e.Cancel = aborted = true;
            Dictionary<MediaTagType, byte[]> mediaTags = new Dictionary<MediaTagType, byte[]>();

            if(dumpRaw)
            {
                dumpLog.WriteLine("Raw CD dumping not yet implemented");
                DicConsole.ErrorWriteLine("Raw CD dumping not yet implemented");
                return;
            }

            // We discarded all discs that falsify a TOC before requesting a real TOC
            // No TOC, no CD (or an empty one)
            dumpLog.WriteLine("Reading full TOC");
            bool tocSense = dev.ReadRawToc(out byte[] cmdBuf, out byte[] senseBuf, 0, dev.Timeout, out _);
            if(!tocSense)
            {
                toc = FullTOC.Decode(cmdBuf);
                if(toc.HasValue)
                {
                    byte[] tmpBuf = new byte[cmdBuf.Length - 2];
                    Array.Copy(cmdBuf, 2, tmpBuf, 0, cmdBuf.Length - 2);
                    mediaTags.Add(MediaTagType.CD_FullTOC, tmpBuf);

                    // ATIP exists on blank CDs
                    dumpLog.WriteLine("Reading ATIP");
                    sense = dev.ReadAtip(out cmdBuf, out senseBuf, dev.Timeout, out _);
                    if(!sense)
                    {
                        ATIP.CDATIP? atip = ATIP.Decode(cmdBuf);
                        if(atip.HasValue)
                        {
                            // Only CD-R and CD-RW have ATIP
                            dskType = atip.Value.DiscType ? MediaType.CDRW : MediaType.CDR;

                            tmpBuf = new byte[cmdBuf.Length - 4];
                            Array.Copy(cmdBuf, 4, tmpBuf, 0, cmdBuf.Length - 4);
                            mediaTags.Add(MediaTagType.CD_ATIP, tmpBuf);
                        }
                    }

                    dumpLog.WriteLine("Reading Disc Information");
                    sense = dev.ReadDiscInformation(out cmdBuf, out senseBuf,
                                                    MmcDiscInformationDataTypes.DiscInformation, dev.Timeout, out _);
                    if(!sense)
                    {
                        DiscInformation.StandardDiscInformation? discInfo = DiscInformation.Decode000b(cmdBuf);
                        if(discInfo.HasValue)
                            if(dskType == MediaType.CD)
                                switch(discInfo.Value.DiscType)
                                {
                                    case 0x10:
                                        dskType = MediaType.CDI;
                                        break;
                                    case 0x20:
                                        dskType = MediaType.CDROMXA;
                                        break;
                                }
                    }

                    int sessions              = 1;
                    int firstTrackLastSession = 0;

                    dumpLog.WriteLine("Reading Session Information");
                    sense = dev.ReadSessionInfo(out cmdBuf, out senseBuf, dev.Timeout, out _);
                    if(!sense)
                    {
                        Session.CDSessionInfo? session = Session.Decode(cmdBuf);
                        if(session.HasValue)
                        {
                            sessions              = session.Value.LastCompleteSession;
                            firstTrackLastSession = session.Value.TrackDescriptors[0].TrackNumber;
                        }
                    }

                    if(dskType == MediaType.CD)
                    {
                        bool hasDataTrack                  = false;
                        bool hasAudioTrack                 = false;
                        bool allFirstSessionTracksAreAudio = true;
                        bool hasVideoTrack                 = false;

                        foreach(FullTOC.TrackDataDescriptor track in toc.Value.TrackDescriptors)
                        {
                            if(track.TNO == 1 && ((TocControl)(track.CONTROL & 0x0D) == TocControl.DataTrack ||
                                                  (TocControl)(track.CONTROL & 0x0D) == TocControl.DataTrackIncremental)
                            ) allFirstSessionTracksAreAudio &= firstTrackLastSession != 1;

                            if((TocControl)(track.CONTROL & 0x0D) == TocControl.DataTrack ||
                               (TocControl)(track.CONTROL & 0x0D) == TocControl.DataTrackIncremental)
                            {
                                hasDataTrack                  =  true;
                                allFirstSessionTracksAreAudio &= track.TNO >= firstTrackLastSession;
                            }
                            else hasAudioTrack = true;

                            hasVideoTrack |= track.ADR == 4;
                        }

                        if(hasDataTrack && hasAudioTrack && allFirstSessionTracksAreAudio && sessions == 2)
                            dskType = MediaType.CDPLUS;
                        if(!hasDataTrack && hasAudioTrack && sessions == 1) dskType = MediaType.CDDA;
                        if(hasDataTrack && !hasAudioTrack && sessions == 1) dskType = MediaType.CDROM;
                        if(hasVideoTrack && !hasDataTrack && sessions == 1) dskType = MediaType.CDV;
                    }

                    dumpLog.WriteLine("Reading PMA");
                    sense = dev.ReadPma(out cmdBuf, out senseBuf, dev.Timeout, out _);
                    if(!sense)
                        if(PMA.Decode(cmdBuf).HasValue)
                        {
                            tmpBuf = new byte[cmdBuf.Length - 4];
                            Array.Copy(cmdBuf, 4, tmpBuf, 0, cmdBuf.Length - 4);
                            mediaTags.Add(MediaTagType.CD_PMA, tmpBuf);
                        }

                    dumpLog.WriteLine("Reading CD-Text from Lead-In");
                    sense = dev.ReadCdText(out cmdBuf, out senseBuf, dev.Timeout, out _);
                    if(!sense)
                        if(CDTextOnLeadIn.Decode(cmdBuf).HasValue)
                        {
                            tmpBuf = new byte[cmdBuf.Length - 4];
                            Array.Copy(cmdBuf, 4, tmpBuf, 0, cmdBuf.Length - 4);
                            mediaTags.Add(MediaTagType.CD_TEXT, tmpBuf);
                        }
                }
            }

            MmcSubchannel supportedSubchannel = MmcSubchannel.Raw;
            dumpLog.WriteLine("Checking if drive supports full raw subchannel reading...");
            DicConsole.WriteLine("Checking if drive supports full raw subchannel reading...");
            readcd = !dev.ReadCd(out byte[] readBuffer, out senseBuf, 0, SECTOR_SIZE + 96, 1, MmcSectorTypes.AllTypes,
                                 false, false, true, MmcHeaderCodes.AllHeaders, true, true, MmcErrorField.None,
                                 supportedSubchannel, dev.Timeout, out _);
            if(readcd)
            {
                dumpLog.WriteLine("Full raw subchannel reading supported...");
                DicConsole.WriteLine("Full raw subchannel reading supported...");
                subSize = 96;
            }
            else
            {
                supportedSubchannel = MmcSubchannel.Q16;
                dumpLog.WriteLine("Checking if drive supports PQ subchannel reading...");
                readcd = !dev.ReadCd(out readBuffer, out senseBuf, 0, SECTOR_SIZE + 16, 1, MmcSectorTypes.AllTypes,
                                     false, false, true, MmcHeaderCodes.AllHeaders, true, true, MmcErrorField.None,
                                     supportedSubchannel, dev.Timeout, out _);

                if(readcd)
                {
                    dumpLog.WriteLine("PQ subchannel reading supported...");
                    dumpLog.WriteLine("WARNING: If disc says CD+G, CD+EG, CD-MIDI, CD Graphics or CD Enhanced Graphics, dump will be incorrect!");
                    DicConsole.WriteLine("PQ subchannel reading supported...");
                    DicConsole
                       .WriteLine("WARNING: If disc says CD+G, CD+EG, CD-MIDI, CD Graphics or CD Enhanced Graphics, dump will be incorrect!");
                    subSize = 16;
                }
                else
                {
                    supportedSubchannel = MmcSubchannel.None;
                    dumpLog.WriteLine("Checking if drive supports reading without subchannel...");
                    readcd = !dev.ReadCd(out readBuffer, out senseBuf, 0, SECTOR_SIZE, 1, MmcSectorTypes.AllTypes,
                                         false, false, true, MmcHeaderCodes.AllHeaders, true, true, MmcErrorField.None,
                                         supportedSubchannel, dev.Timeout, out _);

                    if(!readcd)
                    {
                        dumpLog.WriteLine("Cannot read from disc, not continuing...");
                        DicConsole.ErrorWriteLine("Cannot read from disc, not continuing...");
                        return;
                    }

                    dumpLog.WriteLine("Drive can only read without subchannel...");
                    dumpLog.WriteLine("WARNING: If disc says CD+G, CD+EG, CD-MIDI, CD Graphics or CD Enhanced Graphics, dump will be incorrect!");
                    DicConsole.WriteLine("Drive can only read without subchannel...");
                    DicConsole
                       .WriteLine("WARNING: If disc says CD+G, CD+EG, CD-MIDI, CD Graphics or CD Enhanced Graphics, dump will be incorrect!");
                    subSize = 0;
                }
            }

            // Check if output format supports subchannels
            if(!outputPlugin.SupportedSectorTags.Contains(SectorTagType.CdSectorSubchannel) &&
               supportedSubchannel != MmcSubchannel.None)
            {
                DicConsole.WriteLine("Output format does not support subchannels, {0}continuing...",
                                     force ? "" : "not ");
                dumpLog.WriteLine("Output format does not support subchannels, {0}continuing...", force ? "" : "not ");

                if(!force) return;

                supportedSubchannel = MmcSubchannel.None;
                subSize             = 0;
            }

            TrackSubchannelType subType;

            switch(supportedSubchannel)
            {
                case MmcSubchannel.None:
                    subType = TrackSubchannelType.None;
                    break;
                case MmcSubchannel.Raw:
                    subType = TrackSubchannelType.Raw;
                    break;
                case MmcSubchannel.Q16:
                    subType = TrackSubchannelType.Q16;
                    break;
                default:
                    DicConsole.WriteLine("Handling subchannel type {0} not supported, exiting...", supportedSubchannel);
                    dumpLog.WriteLine("Handling subchannel type {0} not supported, exiting...", supportedSubchannel);
                    return;
            }

            uint blockSize = SECTOR_SIZE + subSize;

            DicConsole.WriteLine("Building track map...");
            dumpLog.WriteLine("Building track map...");

            List<Track>            trackList      = new List<Track>();
            long                   lastSector     = 0;
            Dictionary<byte, byte> trackFlags     = new Dictionary<byte, byte>();
            TrackType              firstTrackType = TrackType.Audio;

            if(toc.HasValue)
            {
                FullTOC.TrackDataDescriptor[] sortedTracks =
                    toc.Value.TrackDescriptors.OrderBy(track => track.POINT).ToArray();

                foreach(FullTOC.TrackDataDescriptor trk in sortedTracks.Where(trk => trk.ADR == 1 || trk.ADR == 4))
                    if(trk.POINT >= 0x01 && trk.POINT <= 0x63)
                    {
                        trackList.Add(new Track
                        {
                            TrackSequence = trk.POINT,
                            TrackSession  = trk.SessionNumber,
                            TrackType =
                                (TocControl)(trk.CONTROL & 0x0D) == TocControl.DataTrack ||
                                (TocControl)(trk.CONTROL & 0x0D) == TocControl.DataTrackIncremental
                                    ? TrackType.Data
                                    : TrackType.Audio,
                            TrackStartSector =
                                (ulong)(trk.PHOUR * 3600 * 75 + trk.PMIN * 60 * 75 + trk.PSEC * 75 + trk.PFRAME - 150),
                            TrackBytesPerSector    = (int)SECTOR_SIZE,
                            TrackRawBytesPerSector = (int)SECTOR_SIZE,
                            TrackSubchannelType    = subType
                        });
                        trackFlags.Add(trk.POINT, trk.CONTROL);
                    }
                    else if(trk.POINT == 0xA2)
                    {
                        int phour, pmin, psec, pframe;
                        if(trk.PFRAME == 0)
                        {
                            pframe = 74;

                            if(trk.PSEC == 0)
                            {
                                psec = 59;

                                if(trk.PMIN == 0)
                                {
                                    pmin  = 59;
                                    phour = trk.PHOUR - 1;
                                }
                                else
                                {
                                    pmin  = trk.PMIN - 1;
                                    phour = trk.PHOUR;
                                }
                            }
                            else
                            {
                                psec  = trk.PSEC - 1;
                                pmin  = trk.PMIN;
                                phour = trk.PHOUR;
                            }
                        }
                        else
                        {
                            pframe = trk.PFRAME - 1;
                            psec   = trk.PSEC;
                            pmin   = trk.PMIN;
                            phour  = trk.PHOUR;
                        }

                        lastSector = phour * 3600 * 75 + pmin * 60 * 75 + psec * 75 + pframe - 150;
                    }
                    else if(trk.POINT == 0xA0 && trk.ADR == 1)
                    {
                        switch(trk.PSEC)
                        {
                            case 0x10:
                                dskType = MediaType.CDI;
                                break;
                            case 0x20:
                                dskType = MediaType.CDROMXA;
                                break;
                        }

                        firstTrackType =
                            (TocControl)(trk.CONTROL & 0x0D) == TocControl.DataTrack ||
                            (TocControl)(trk.CONTROL & 0x0D) == TocControl.DataTrackIncremental
                                ? TrackType.Data
                                : TrackType.Audio;
                    }
            }
            else
            {
                DicConsole.WriteLine("Cannot read RAW TOC, requesting processed one...");
                dumpLog.WriteLine("Cannot read RAW TOC, requesting processed one...");
                tocSense = dev.ReadToc(out cmdBuf, out senseBuf, false, 0, dev.Timeout, out _);

                TOC.CDTOC? oldToc = TOC.Decode(cmdBuf);
                if((tocSense || !oldToc.HasValue) && !force)
                {
                    DicConsole
                       .WriteLine("Could not read TOC, if you want to continue, use force, and will try from LBA 0 to 360000...");
                    dumpLog.WriteLine("Could not read TOC, if you want to continue, use force, and will try from LBA 0 to 360000...");
                    return;
                }

                foreach(TOC.CDTOCTrackDataDescriptor trk in oldToc
                                                           .Value.TrackDescriptors.OrderBy(t => t.TrackNumber)
                                                           .Where(trk => trk.ADR == 1 || trk.ADR == 4))
                    if(trk.TrackNumber >= 0x01 && trk.TrackNumber <= 0x63)
                    {
                        trackList.Add(new Track
                        {
                            TrackSequence = trk.TrackNumber,
                            TrackSession  = 1,
                            TrackType =
                                (TocControl)(trk.CONTROL & 0x0D) == TocControl.DataTrack ||
                                (TocControl)(trk.CONTROL & 0x0D) == TocControl.DataTrackIncremental
                                    ? TrackType.Data
                                    : TrackType.Audio,
                            TrackStartSector       = trk.TrackStartAddress,
                            TrackBytesPerSector    = (int)SECTOR_SIZE,
                            TrackRawBytesPerSector = (int)SECTOR_SIZE,
                            TrackSubchannelType    = subType
                        });
                        trackFlags.Add(trk.TrackNumber, trk.CONTROL);
                    }
                    else if(trk.TrackNumber == 0xAA)
                    {
                        firstTrackType =
                            (TocControl)(trk.CONTROL & 0x0D) == TocControl.DataTrack ||
                            (TocControl)(trk.CONTROL & 0x0D) == TocControl.DataTrackIncremental
                                ? TrackType.Data
                                : TrackType.Audio;
                        lastSector = trk.TrackStartAddress - 1;
                    }
            }

            if(trackList.Count == 0)
            {
                DicConsole.WriteLine("No tracks found, adding a single track from 0 to Lead-Out");
                dumpLog.WriteLine("No tracks found, adding a single track from 0 to Lead-Out");

                trackList.Add(new Track
                {
                    TrackSequence          = 1,
                    TrackSession           = 1,
                    TrackType              = firstTrackType,
                    TrackStartSector       = 0,
                    TrackBytesPerSector    = (int)SECTOR_SIZE,
                    TrackRawBytesPerSector = (int)SECTOR_SIZE,
                    TrackSubchannelType    = subType
                });
                trackFlags.Add(1, (byte)(firstTrackType == TrackType.Audio ? 0 : 4));
            }

            if(lastSector == 0)
            {
                if(!force)
                {
                    DicConsole
                       .WriteLine("Could not find Lead-Out, if you want to continue use force option and will continue until 360000 sectors...");
                    dumpLog.WriteLine("Could not find Lead-Out, if you want to continue use force option and will continue until 360000 sectors...");
                    return;
                }

                DicConsole.WriteLine("WARNING: Could not find Lead-Out start, will try to read up to 360000 sectors, probably will fail before...");
                dumpLog.WriteLine("WARNING: Could not find Lead-Out start, will try to read up to 360000 sectors, probably will fail before...");
                lastSector = 360000;
            }

            Track[] tracks                                                      = trackList.ToArray();
            for(int t = 1; t < tracks.Length; t++) tracks[t - 1].TrackEndSector = tracks[t].TrackStartSector - 1;

            tracks[tracks.Length              - 1].TrackEndSector = (ulong)lastSector;
            ulong blocks = (ulong)(lastSector + 1);

            if(blocks == 0)
            {
                DicConsole.ErrorWriteLine("Cannot dump blank media.");
                return;
            }

            // Check if output format supports all disc tags we have retrieved so far
            foreach(MediaTagType tag in mediaTags.Keys)
            {
                if(outputPlugin.SupportedMediaTags.Contains(tag)) continue;

                DicConsole.WriteLine("Output format does not support {0}, {1}continuing...", tag, force ? "" : "not ");
                dumpLog.WriteLine("Output format does not support {0}, {1}continuing...", tag, force ? "" : "not ");

                if(!force) return;
            }

            // Check mode for tracks
            for(int t = 0; t < tracks.Length; t++)
            {
                if(tracks[t].TrackType == TrackType.Audio) continue;

                dumpLog.WriteLine("Checking mode for track {0}...", tracks[t].TrackSequence);
                DicConsole.WriteLine("Checking mode for track {0}...", tracks[t].TrackSequence);

                readcd = !dev.ReadCd(out readBuffer, out senseBuf, (uint)tracks[t].TrackStartSector, blockSize, 1,
                                     MmcSectorTypes.AllTypes, false, false, true, MmcHeaderCodes.AllHeaders, true, true,
                                     MmcErrorField.None, supportedSubchannel, dev.Timeout, out _);

                if(!readcd)
                {
                    dumpLog.WriteLine("Unable to guess mode for track {0}, continuing...", tracks[t].TrackSequence);
                    DicConsole.WriteLine("Unable to guess mode for track {0}, continuing...", tracks[t].TrackSequence);
                    continue;
                }

                switch(readBuffer[15])
                {
                    case 1:
                        DicConsole.WriteLine("Track {0} is MODE1", tracks[t].TrackSequence);
                        dumpLog.WriteLine("Track {0} is MODE1", tracks[t].TrackSequence);
                        tracks[t].TrackType = TrackType.CdMode1;
                        break;
                    case 2:
                        DicConsole.WriteLine("Track {0} is MODE2", tracks[t].TrackSequence);
                        dumpLog.WriteLine("Track {0} is MODE2", tracks[t].TrackSequence);
                        tracks[t].TrackType = TrackType.CdMode2Formless;
                        break;
                    default:
                        DicConsole.WriteLine("Track {0} is unknown mode {1}", tracks[t].TrackSequence, readBuffer[15]);
                        dumpLog.WriteLine("Track {0} is unknown mode {1}", tracks[t].TrackSequence, readBuffer[15]);
                        break;
                }
            }

            // Check if something prevents from dumping the Lead-in
            if(dumpLeadIn)
            {
                if(dev.PlatformId == PlatformID.FreeBSD)
                {
                    dumpLog.WriteLine("FreeBSD panics when reading CD Lead-in, see upstream bug #224253. {0}continuing",
                                      force ? "" : "Not ");
                    DicConsole
                       .ErrorWriteLine("FreeBSD panics when reading CD Lead-in, see upstream bug #224253. {0}continuing",
                                       force ? "" : "Not ");

                    if(!force) return;

                    dumpLeadIn = false;
                }

                if(!outputPlugin.SupportedMediaTags.Contains(MediaTagType.CD_LeadIn))
                {
                    DicConsole.WriteLine("Output format does not support CD Lead-in, {0}continuing...",
                                         force ? "" : "not ");
                    dumpLog.WriteLine("Output format does not support CD Lead-in, {0}continuing...",
                                      force ? "" : "not ");

                    if(!force) return;

                    dumpLeadIn = false;
                }
            }

            DumpHardwareType currentTry = null;
            ExtentsULong     extents    = null;
            ResumeSupport.Process(true, true, blocks, dev.Manufacturer, dev.Model, dev.Serial, dev.PlatformId,
                                  ref resume, ref currentTry, ref extents);
            if(currentTry == null || extents == null)
                throw new InvalidOperationException("Could not process resume file, not continuing...");

            // Try to read the Lead-in
            if(dumpLeadIn)
            {
                DicConsole.WriteLine("Trying to read Lead-In...");
                bool         gotLeadIn         = false;
                int          leadInSectorsGood = 0;
                MemoryStream leadinMs          = new MemoryStream();

                readBuffer = null;

                dumpLog.WriteLine("Reading Lead-in");
                for(int leadInBlock = -150; leadInBlock < 0 && resume.NextBlock == 0; leadInBlock++)
                {
                    if(aborted)
                    {
                        dumpLog.WriteLine("Aborted!");
                        break;
                    }

                    #pragma warning disable RECS0018 // Comparison of floating point numbers with equality operator
                    if(currentSpeed > maxSpeed && currentSpeed != 0) maxSpeed = currentSpeed;
                    if(currentSpeed < minSpeed && currentSpeed != 0) minSpeed = currentSpeed;
                    #pragma warning restore RECS0018 // Comparison of floating point numbers with equality operator

                    DicConsole.Write("\rTrying to read lead-in sector {0} ({1:F3} MiB/sec.)", leadInBlock,
                                     currentSpeed);

                    sense = dev.ReadCd(out readBuffer, out senseBuf, (uint)leadInBlock, blockSize, 1,
                                       MmcSectorTypes.AllTypes, false, false, true, MmcHeaderCodes.AllHeaders, true,
                                       true, MmcErrorField.None, supportedSubchannel, dev.Timeout,
                                       out double cmdDuration);

                    if(!sense && !dev.Error)
                    {
                        leadinMs.Write(readBuffer, 0, (int)blockSize);
                        gotLeadIn = true;
                        leadInSectorsGood++;
                    }
                    else
                    {
                        // Write empty data
                        if(gotLeadIn) leadinMs.Write(new byte[blockSize], 0, (int)blockSize);
                    }

                    double newSpeed                               = blockSize / (double)1048576 / (cmdDuration / 1000);
                    if(!double.IsInfinity(newSpeed)) currentSpeed = newSpeed;
                }

                if(leadInSectorsGood > 0) mediaTags.Add(MediaTagType.CD_LeadIn, leadinMs.ToArray());

                DicConsole.WriteLine();
                DicConsole.WriteLine("Got {0} lead-in sectors.", leadInSectorsGood);
                dumpLog.WriteLine("Got {0} Lead-in sectors.", leadInSectorsGood);

                leadinMs.Close();
            }

            // Try how many blocks are readable at once
            while(true)
            {
                if(readcd)
                {
                    sense = dev.ReadCd(out readBuffer, out senseBuf, 0, blockSize, blocksToRead,
                                       MmcSectorTypes.AllTypes, false, false, true, MmcHeaderCodes.AllHeaders, true,
                                       true, MmcErrorField.None, supportedSubchannel, dev.Timeout, out _);
                    if(dev.Error || sense) blocksToRead /= 2;
                }

                if(!dev.Error || blocksToRead == 1) break;
            }

            if(dev.Error || sense)
            {
                DicConsole.WriteLine("Device error {0} trying to guess ideal transfer length.", dev.LastError);
                DicConsole.ErrorWriteLine("Device error {0} trying to guess ideal transfer length.", dev.LastError);
                return;
            }

            DicConsole.WriteLine("Reading {0} sectors at a time.", blocksToRead);

            dumpLog.WriteLine("Device reports {0} blocks ({1} bytes).",      blocks, blocks * blockSize);
            dumpLog.WriteLine("Device can read {0} blocks at a time.",       blocksToRead);
            dumpLog.WriteLine("Device reports {0} bytes per logical block.", blockSize);
            dumpLog.WriteLine("SCSI device type: {0}.",                      dev.ScsiType);
            dumpLog.WriteLine("Media identified as {0}.",                    dskType);

            DicConsole.WriteLine("Device reports {0} blocks ({1} bytes).",      blocks, blocks * blockSize);
            DicConsole.WriteLine("Device can read {0} blocks at a time.",       blocksToRead);
            DicConsole.WriteLine("Device reports {0} bytes per logical block.", blockSize);
            DicConsole.WriteLine("SCSI device type: {0}.",                      dev.ScsiType);
            DicConsole.WriteLine("Media identified as {0}.",                    dskType);

            MhddLog mhddLog = new MhddLog(outputPrefix + ".mhddlog.bin", dev, blocks, blockSize, blocksToRead);
            IbgLog  ibgLog  = new IbgLog(outputPrefix  + ".ibg", 0x0008);
            bool    ret     = outputPlugin.Create(outputPath, dskType, formatOptions, blocks, SECTOR_SIZE);

            // Cannot create image
            if(!ret)
            {
                dumpLog.WriteLine("Error creating output image, not continuing.");
                dumpLog.WriteLine(outputPlugin.ErrorMessage);
                DicConsole.ErrorWriteLine("Error creating output image, not continuing.");
                DicConsole.ErrorWriteLine(outputPlugin.ErrorMessage);
                return;
            }

            // Send tracklist to output plugin. This may fail if subchannel is set but unsupported.
            ret = outputPlugin.SetTracks(tracks.ToList());
            if(!ret && supportedSubchannel == MmcSubchannel.None)
            {
                dumpLog.WriteLine("Error sending tracks to output image, not continuing.");
                dumpLog.WriteLine(outputPlugin.ErrorMessage);
                DicConsole.ErrorWriteLine("Error sending tracks to output image, not continuing.");
                DicConsole.ErrorWriteLine(outputPlugin.ErrorMessage);
                return;
            }

            // If a subchannel is supported, check if output plugin allows us to write it.
            if(supportedSubchannel != MmcSubchannel.None)
            {
                sense = dev.ReadCd(out readBuffer, out senseBuf, 0, blockSize, 1, MmcSectorTypes.AllTypes, false, false,
                                   true, MmcHeaderCodes.AllHeaders, true, true, MmcErrorField.None, supportedSubchannel,
                                   dev.Timeout, out _);

                byte[] tmpBuf = new byte[subSize];
                Array.Copy(readBuffer, SECTOR_SIZE, tmpBuf, 0, subSize);

                ret = outputPlugin.WriteSectorTag(tmpBuf, 0, SectorTagType.CdSectorSubchannel);

                if(!ret)
                {
                    DicConsole.WriteLine("Error writing subchannel to output image, {0}continuing...",
                                         force ? "" : "not ");
                    dumpLog.WriteLine("Error writing subchannel to output image, {0}continuing...",
                                      force ? "" : "not ");

                    if(!force) return;

                    supportedSubchannel = MmcSubchannel.None;
                    subSize             = 0;
                    blockSize           = SECTOR_SIZE + subSize;
                    for(int t = 0; t < tracks.Length; t++) tracks[t].TrackSubchannelType = TrackSubchannelType.None;
                    ret = outputPlugin.SetTracks(tracks.ToList());
                    if(!ret)
                    {
                        dumpLog.WriteLine("Error sending tracks to output image, not continuing.");
                        dumpLog.WriteLine(outputPlugin.ErrorMessage);
                        DicConsole.ErrorWriteLine("Error sending tracks to output image, not continuing.");
                        DicConsole.ErrorWriteLine(outputPlugin.ErrorMessage);
                        return;
                    }
                }
            }

            // Set track flags
            foreach(KeyValuePair<byte, byte> kvp in trackFlags)
            {
                Track track = tracks.FirstOrDefault(t => t.TrackSequence == kvp.Key);

                if(track.TrackSequence == 0) continue;

                dumpLog.WriteLine("Setting flags for track {0}...", track.TrackSequence);
                DicConsole.WriteLine("Setting flags for track {0}...", track.TrackSequence);
                outputPlugin.WriteSectorTag(new[] {kvp.Value}, track.TrackStartSector, SectorTagType.CdTrackFlags);
            }

            // Set MCN
            sense = dev.ReadMcn(out string mcn, out _, out _, dev.Timeout, out _);
            if(!sense && mcn != null && mcn != "0000000000000")
                if(outputPlugin.WriteMediaTag(Encoding.ASCII.GetBytes(mcn), MediaTagType.CD_MCN))
                {
                    DicConsole.WriteLine("Setting disc Media Catalogue Number to {0}", mcn);
                    dumpLog.WriteLine("Setting disc Media Catalogue Number to {0}", mcn);
                }

            // Set ISRCs
            foreach(Track trk in tracks)
            {
                sense = dev.ReadIsrc((byte)trk.TrackSequence, out string isrc, out _, out _, dev.Timeout, out _);
                if(sense || isrc == null || isrc == "000000000000") continue;

                if(outputPlugin.WriteSectorTag(Encoding.ASCII.GetBytes(isrc), trk.TrackStartSector,
                                               SectorTagType.CdTrackIsrc))
                {
                    DicConsole.WriteLine("Setting ISRC for track {0} to {1}", trk.TrackSequence, isrc);
                    dumpLog.WriteLine("Setting ISRC for track {0} to {1}", trk.TrackSequence, isrc);
                }
            }

            if(resume.NextBlock > 0) dumpLog.WriteLine("Resuming from block {0}.", resume.NextBlock);

            double imageWriteDuration = 0;

            if(skip < blocksToRead) skip = blocksToRead;
            bool newTrim                 = false;

            // Start reading
            start = DateTime.UtcNow;
            for(int t = 0; t < tracks.Length; t++)
            {
                dumpLog.WriteLine("Reading track {0}", t + 1);

                for(ulong i = resume.NextBlock; i <= tracks[t].TrackEndSector; i += blocksToRead)
                {
                    if(aborted)
                    {
                        currentTry.Extents = ExtentsConverter.ToMetadata(extents);
                        dumpLog.WriteLine("Aborted!");
                        break;
                    }

                    double cmdDuration = 0;

                    if(tracks[t].TrackEndSector + 1 - i < blocksToRead)
                        blocksToRead = (uint)(tracks[t].TrackEndSector + 1 - i);

                    #pragma warning disable RECS0018 // Comparison of floating point numbers with equality operator
                    if(currentSpeed > maxSpeed && currentSpeed != 0) maxSpeed = currentSpeed;
                    if(currentSpeed < minSpeed && currentSpeed != 0) minSpeed = currentSpeed;
                    #pragma warning restore RECS0018 // Comparison of floating point numbers with equality operator

                    DicConsole.Write("\rReading sector {0} of {1} at track {3} ({2:F3} MiB/sec.)", i, blocks,
                                     currentSpeed, t + 1);

                    if(readcd)
                    {
                        sense = dev.ReadCd(out readBuffer, out senseBuf, (uint)i, blockSize, blocksToRead,
                                           MmcSectorTypes.AllTypes, false, false, true, MmcHeaderCodes.AllHeaders, true,
                                           true, MmcErrorField.None, supportedSubchannel, dev.Timeout, out cmdDuration);
                        totalDuration += cmdDuration;
                    }

                    if(!sense && !dev.Error)
                    {
                        mhddLog.Write(i, cmdDuration);
                        ibgLog.Write(i, currentSpeed * 1024);
                        extents.Add(i, blocksToRead, true);
                        DateTime writeStart = DateTime.Now;
                        if(supportedSubchannel != MmcSubchannel.None)
                        {
                            byte[] data = new byte[SECTOR_SIZE * blocksToRead];
                            byte[] sub  = new byte[subSize     * blocksToRead];

                            for(int b = 0; b < blocksToRead; b++)
                            {
                                Array.Copy(readBuffer, (int)(0 + b * blockSize), data, SECTOR_SIZE * b,
                                           SECTOR_SIZE);
                                Array.Copy(readBuffer, (int)(SECTOR_SIZE + b * blockSize), sub, subSize * b,
                                           subSize);
                            }

                            outputPlugin.WriteSectorsLong(data, i, blocksToRead);
                            outputPlugin.WriteSectorsTag(sub, i, blocksToRead, SectorTagType.CdSectorSubchannel);
                        }
                        else outputPlugin.WriteSectorsLong(readBuffer, i, blocksToRead);

                        imageWriteDuration += (DateTime.Now - writeStart).TotalSeconds;
                    }
                    else
                    {
                        // TODO: Reset device after X errors
                        if(stopOnError) return; // TODO: Return more cleanly

                        if(i + skip > blocks) skip = (uint)(blocks - i);

                        // Write empty data
                        DateTime writeStart = DateTime.Now;
                        if(supportedSubchannel != MmcSubchannel.None)
                        {
                            outputPlugin.WriteSectorsLong(new byte[SECTOR_SIZE * skip], i, skip);
                            outputPlugin.WriteSectorsTag(new byte[subSize * skip], i, skip,
                                                         SectorTagType.CdSectorSubchannel);
                        }
                        else outputPlugin.WriteSectorsLong(new byte[blockSize * skip], i, skip);

                        imageWriteDuration += (DateTime.Now - writeStart).TotalSeconds;

                        for(ulong b = i; b < i + skip; b++) resume.BadBlocks.Add(b);

                        DicConsole.DebugWriteLine("Dump-Media", "READ error:\n{0}", Sense.PrettifySense(senseBuf));
                        mhddLog.Write(i, cmdDuration < 500 ? 65535 : cmdDuration);

                        ibgLog.Write(i, 0);
                        dumpLog.WriteLine("Skipping {0} blocks from errored block {1}.", skip, i);
                        i       += skip - blocksToRead;
                        newTrim =  true;
                    }

                    double newSpeed =
                        (double)blockSize * blocksToRead / 1048576 / (cmdDuration / 1000);
                    if(!double.IsInfinity(newSpeed)) currentSpeed = newSpeed;
                    resume.NextBlock = i + blocksToRead;
                }
            }

            DicConsole.WriteLine();
            end = DateTime.UtcNow;
            mhddLog.Close();
            ibgLog.Close(dev, blocks, blockSize, (end - start).TotalSeconds, currentSpeed * 1024,
                         blockSize * (double)(blocks + 1) / 1024                          / (totalDuration / 1000),
                         devicePath);
            dumpLog.WriteLine("Dump finished in {0} seconds.", (end - start).TotalSeconds);
            dumpLog.WriteLine("Average dump speed {0:F3} KiB/sec.",
                              (double)blockSize * (double)(blocks + 1) / 1024 / (totalDuration / 1000));
            dumpLog.WriteLine("Average write speed {0:F3} KiB/sec.",
                              (double)blockSize * (double)(blocks + 1) / 1024 / imageWriteDuration);

            #region Compact Disc Error trimming
            if(resume.BadBlocks.Count > 0 && !aborted && !notrim && newTrim)
            {
                start = DateTime.UtcNow;
                dumpLog.WriteLine("Trimming bad sectors");

                ulong[] tmpArray = resume.BadBlocks.ToArray();
                foreach(ulong badSector in tmpArray)
                {
                    if(aborted)
                    {
                        currentTry.Extents = ExtentsConverter.ToMetadata(extents);
                        dumpLog.WriteLine("Aborted!");
                        break;
                    }

                    DicConsole.Write("\rTrimming sector {0}", badSector);

                    if(readcd)
                    {
                        sense = true;
                        sense = dev.ReadCd(out readBuffer, out senseBuf, (uint)badSector, blockSize, 1,
                                           MmcSectorTypes.AllTypes, false, false, true, MmcHeaderCodes.AllHeaders, true,
                                           true, MmcErrorField.None, supportedSubchannel, dev.Timeout,
                                           out double cmdDuration);
                        totalDuration += cmdDuration;
                    }

                    if(sense || dev.Error) continue;

                    if(!sense && !dev.Error)
                    {
                        resume.BadBlocks.Remove(badSector);
                        extents.Add(badSector);
                    }

                    if(supportedSubchannel != MmcSubchannel.None)
                    {
                        byte[] data = new byte[SECTOR_SIZE];
                        byte[] sub  = new byte[subSize];
                        Array.Copy(readBuffer, 0,           data, 0, SECTOR_SIZE);
                        Array.Copy(readBuffer, SECTOR_SIZE, sub,  0, subSize);
                        outputPlugin.WriteSectorLong(data, badSector);
                        outputPlugin.WriteSectorTag(sub, badSector, SectorTagType.CdSectorSubchannel);
                    }
                    else outputPlugin.WriteSectorLong(readBuffer, badSector);
                }

                DicConsole.WriteLine();
                end = DateTime.UtcNow;
                dumpLog.WriteLine("Trimmming finished in {0} seconds.", (end - start).TotalSeconds);
            }
            #endregion Compact Disc Error trimming

            #region Compact Disc Error handling
            if(resume.BadBlocks.Count > 0 && !aborted && retryPasses > 0)
            {
                int  pass              = 1;
                bool forward           = true;
                bool runningPersistent = false;

                Modes.ModePage? currentModePage = null;
                byte[]          md6;
                byte[]          md10;

                if(persistent)
                {
                    Modes.ModePage_01_MMC pgMmc;
                    
                    sense = dev.ModeSense6(out readBuffer, out _, false, ScsiModeSensePageControl.Current, 0x01,
                                           dev.Timeout, out _);
                    if(sense)
                    {
                        sense = dev.ModeSense10(out readBuffer, out _, false, ScsiModeSensePageControl.Current,
                                                0x01, dev.Timeout, out _);

                        if(!sense)
                        {
                            Modes.DecodedMode? dcMode10 =
                                Modes.DecodeMode10(readBuffer, PeripheralDeviceTypes.MultiMediaDevice);
                            
                            if(dcMode10.HasValue)
                            {
                                foreach(Modes.ModePage modePage in dcMode10.Value.Pages)
                                    if(modePage.Page == 0x01 && modePage.Subpage == 0x00) currentModePage = modePage;
                            }
                        }
                    }
                    else
                    {
                        Modes.DecodedMode? dcMode6 =
                            Modes.DecodeMode6(readBuffer, PeripheralDeviceTypes.MultiMediaDevice);

                        if(dcMode6.HasValue)
                        {
                            foreach(Modes.ModePage modePage in dcMode6.Value.Pages)
                                if(modePage.Page == 0x01 && modePage.Subpage == 0x00)
                                    currentModePage = modePage;
                        }
                    }

                    if(currentModePage == null)
                    {
                            
                        pgMmc =
                            new Modes.ModePage_01_MMC {PS = false, ReadRetryCount = 32, Parameter = 0x00};
                        currentModePage = new Modes.ModePage
                        {
                            Page         = 0x01,
                            Subpage      = 0x00,
                            PageResponse = Modes.EncodeModePage_01_MMC(pgMmc)
                        };
                    }
                    
                    pgMmc =
                        new Modes.ModePage_01_MMC {PS = false, ReadRetryCount = 255, Parameter = 0x20};
                    Modes.DecodedMode md = new Modes.DecodedMode
                    {
                        Header = new Modes.ModeHeader(),
                        Pages = new[]
                        {
                            new Modes.ModePage
                            {
                                Page         = 0x01,
                                Subpage      = 0x00,
                                PageResponse = Modes.EncodeModePage_01_MMC(pgMmc)
                            }
                        }
                    };
                    md6  = Modes.EncodeMode6(md, dev.ScsiType);
                    md10 = Modes.EncodeMode10(md, dev.ScsiType);

                    dumpLog.WriteLine("Sending MODE SELECT to drive (return damaged blocks).");
                    sense = dev.ModeSelect(md6, out senseBuf, true, false, dev.Timeout, out _);
                    if(sense) sense = dev.ModeSelect10(md10, out senseBuf, true, false, dev.Timeout, out _);

                    if(sense)
                    {
                        DicConsole
                           .WriteLine("Drive did not accept MODE SELECT command for persistent error reading, try another drive.");
                        DicConsole.DebugWriteLine("Error: {0}", Sense.PrettifySense(senseBuf));
                        dumpLog.WriteLine("Drive did not accept MODE SELECT command for persistent error reading, try another drive.");
                    }
                    else runningPersistent = true;
                }

                cdRepeatRetry:
                ulong[]     tmpArray              = resume.BadBlocks.ToArray();
                List<ulong> sectorsNotEvenPartial = new List<ulong>();
                foreach(ulong badSector in tmpArray)
                {
                    if(aborted)
                    {
                        currentTry.Extents = ExtentsConverter.ToMetadata(extents);
                        dumpLog.WriteLine("Aborted!");
                        break;
                    }

                    DicConsole.Write("\rRetrying sector {0}, pass {1}, {3}{2}", badSector, pass,
                                     forward ? "forward" : "reverse",
                                     runningPersistent ? "recovering partial data, " : "");

                    if(readcd)
                    {
                        sense = true;
                        sense = dev.ReadCd(out readBuffer, out senseBuf, (uint)badSector, blockSize, 1,
                                           MmcSectorTypes.AllTypes, false, false, true, MmcHeaderCodes.AllHeaders, true,
                                           true, MmcErrorField.None, supportedSubchannel, dev.Timeout,
                                           out double cmdDuration);
                        totalDuration += cmdDuration;
                    }

                    if(sense || dev.Error)
                    {
                        if(!runningPersistent) continue;

                        FixedSense? decSense = Sense.DecodeFixed(senseBuf);

                        // MEDIUM ERROR, retry with ignore error below
                        if(decSense.HasValue && decSense.Value.ASC == 0x11)
                            if(!sectorsNotEvenPartial.Contains(badSector))
                                sectorsNotEvenPartial.Add(badSector);
                    }

                    if(!sense && !dev.Error)
                    {
                        resume.BadBlocks.Remove(badSector);
                        extents.Add(badSector);
                        dumpLog.WriteLine("Correctly retried sector {0} in pass {1}.", badSector, pass);
                        sectorsNotEvenPartial.Remove(badSector);
                    }

                    if(supportedSubchannel != MmcSubchannel.None)
                    {
                        byte[] data = new byte[SECTOR_SIZE];
                        byte[] sub  = new byte[subSize];
                        Array.Copy(readBuffer, 0,           data, 0, SECTOR_SIZE);
                        Array.Copy(readBuffer, SECTOR_SIZE, sub,  0, subSize);
                        outputPlugin.WriteSectorLong(data, badSector);
                        outputPlugin.WriteSectorTag(sub, badSector, SectorTagType.CdSectorSubchannel);
                    }
                    else outputPlugin.WriteSectorLong(readBuffer, badSector);
                }

                if(pass < retryPasses && !aborted && resume.BadBlocks.Count > 0)
                {
                    pass++;
                    forward = !forward;
                    resume.BadBlocks.Sort();
                    resume.BadBlocks.Reverse();
                    goto cdRepeatRetry;
                }

                // Try to ignore read errors, on some drives this allows to recover partial even if damaged data
                if(persistent && sectorsNotEvenPartial.Count > 0)
                {
                    Modes.ModePage_01_MMC pgMmc =
                        new Modes.ModePage_01_MMC {PS = false, ReadRetryCount = 255, Parameter = 0x01};
                    Modes.DecodedMode md = new Modes.DecodedMode
                    {
                        Header = new Modes.ModeHeader(),
                        Pages = new[]
                        {
                            new Modes.ModePage
                            {
                                Page         = 0x01,
                                Subpage      = 0x00,
                                PageResponse = Modes.EncodeModePage_01_MMC(pgMmc)
                            }
                        }
                    };
                    md6  = Modes.EncodeMode6(md, dev.ScsiType);
                    md10 = Modes.EncodeMode10(md, dev.ScsiType);

                    dumpLog.WriteLine("Sending MODE SELECT to drive (ignore error correction).");
                    sense = dev.ModeSelect(md6, out senseBuf, true, false, dev.Timeout, out _);
                    if(sense) sense = dev.ModeSelect10(md10, out senseBuf, true, false, dev.Timeout, out _);

                    if(!sense)
                    {
                        runningPersistent = true;
                        DicConsole.WriteLine();

                        tmpArray = resume.BadBlocks.ToArray();
                        foreach(ulong badSector in sectorsNotEvenPartial)
                        {
                            if(aborted)
                            {
                                currentTry.Extents = ExtentsConverter.ToMetadata(extents);
                                dumpLog.WriteLine("Aborted!");
                                break;
                            }

                            DicConsole.Write("\rTrying to get partial data for sector {0}", badSector);

                            if(readcd)
                            {
                                sense = dev.ReadCd(out readBuffer, out senseBuf, (uint)badSector, blockSize, 1,
                                                   MmcSectorTypes.AllTypes, false, false, true,
                                                   MmcHeaderCodes.AllHeaders, true, true, MmcErrorField.None,
                                                   supportedSubchannel, dev.Timeout, out double cmdDuration);
                                totalDuration += cmdDuration;
                            }

                            if(!sense && !dev.Error)
                            {
                                dumpLog.WriteLine("Got partial data for sector {0} in pass {1}.", badSector, pass);

                                if(supportedSubchannel != MmcSubchannel.None)
                                {
                                    byte[] data = new byte[SECTOR_SIZE];
                                    byte[] sub  = new byte[subSize];
                                    Array.Copy(readBuffer, 0,           data, 0, SECTOR_SIZE);
                                    Array.Copy(readBuffer, SECTOR_SIZE, sub,  0, subSize);
                                    outputPlugin.WriteSectorLong(data, badSector);
                                    outputPlugin.WriteSectorTag(sub, badSector, SectorTagType.CdSectorSubchannel);
                                }
                                else outputPlugin.WriteSectorLong(readBuffer, badSector);
                            }
                        }
                    }
                }

                if(runningPersistent && currentModePage.HasValue)
                {
                    Modes.DecodedMode md = new Modes.DecodedMode
                    {
                        Header = new Modes.ModeHeader(),
                        Pages  = new[] {currentModePage.Value}
                    };
                    md6  = Modes.EncodeMode6(md, dev.ScsiType);
                    md10 = Modes.EncodeMode10(md, dev.ScsiType);

                    dumpLog.WriteLine("Sending MODE SELECT to drive (return device to previous status).");
                    sense = dev.ModeSelect(md6, out senseBuf, true, false, dev.Timeout, out _);
                    if(sense) dev.ModeSelect10(md10, out senseBuf, true, false, dev.Timeout, out _);
                }

                DicConsole.WriteLine();
            }
            #endregion Compact Disc Error handling

            // Write media tags to image
            if(!aborted)
                foreach(KeyValuePair<MediaTagType, byte[]> tag in mediaTags)
                {
                    ret = outputPlugin.WriteMediaTag(tag.Value, tag.Key);

                    if(ret || force) continue;

                    // Cannot write tag to image
                    dumpLog.WriteLine($"Cannot write tag {tag.Key}.");
                    throw new ArgumentException(outputPlugin.ErrorMessage);
                }

            resume.BadBlocks.Sort();
            foreach(ulong bad in resume.BadBlocks) dumpLog.WriteLine("Sector {0} could not be read.", bad);
            currentTry.Extents = ExtentsConverter.ToMetadata(extents);

            outputPlugin.SetDumpHardware(resume.Tries);
            if(preSidecar != null) outputPlugin.SetCicmMetadata(preSidecar);
            dumpLog.WriteLine("Closing output file.");
            DicConsole.WriteLine("Closing output file.");
            DateTime closeStart = DateTime.Now;
            outputPlugin.Close();
            DateTime closeEnd = DateTime.Now;
            dumpLog.WriteLine("Closed in {0} seconds.", (closeEnd - closeStart).TotalSeconds);

            if(aborted)
            {
                dumpLog.WriteLine("Aborted!");
                return;
            }

            double totalChkDuration = 0;
            if(!nometadata)
            {
                dumpLog.WriteLine("Creating sidecar.");
                FiltersList filters     = new FiltersList();
                IFilter     filter      = filters.GetFilter(outputPath);
                IMediaImage inputPlugin = ImageFormat.Detect(filter);
                if(!inputPlugin.Open(filter)) throw new ArgumentException("Could not open created image.");

                DateTime         chkStart = DateTime.UtcNow;
                CICMMetadataType sidecar  = Sidecar.Create(inputPlugin, outputPath, filter.Id, encoding);
                end = DateTime.UtcNow;

                totalChkDuration = (end - chkStart).TotalMilliseconds;
                dumpLog.WriteLine("Sidecar created in {0} seconds.", (end - chkStart).TotalSeconds);
                dumpLog.WriteLine("Average checksum speed {0:F3} KiB/sec.",
                                  (double)blockSize * (double)(blocks + 1) / 1024 / (totalChkDuration / 1000));

                if(preSidecar != null)
                {
                    preSidecar.OpticalDisc = sidecar.OpticalDisc;
                    sidecar                = preSidecar;
                }

                List<(ulong start, string type)> filesystems = new List<(ulong start, string type)>();
                if(sidecar.OpticalDisc[0].Track != null)
                    filesystems.AddRange(from xmlTrack in sidecar.OpticalDisc[0].Track
                                         where xmlTrack.FileSystemInformation != null
                                         from partition in xmlTrack.FileSystemInformation
                                         where partition.FileSystems != null
                                         from fileSystem in partition.FileSystems
                                         select ((ulong)partition.StartSector, fileSystem.Type));

                if(filesystems.Count > 0)
                    foreach(var filesystem in filesystems.Select(o => new {o.start, o.type}).Distinct())
                        dumpLog.WriteLine("Found filesystem {0} at sector {1}", filesystem.type, filesystem.start);

                sidecar.OpticalDisc[0].Dimensions = Dimensions.DimensionsFromMediaType(dskType);
                Metadata.MediaType.MediaTypeToString(dskType, out string xmlDskTyp, out string xmlDskSubTyp);
                sidecar.OpticalDisc[0].DiscType          = xmlDskTyp;
                sidecar.OpticalDisc[0].DiscSubType       = xmlDskSubTyp;
                sidecar.OpticalDisc[0].DumpHardwareArray = resume.Tries.ToArray();

                foreach(KeyValuePair<MediaTagType, byte[]> tag in mediaTags)
                    if(outputPlugin.SupportedMediaTags.Contains(tag.Key))
                        Mmc.AddMediaTagToSidecar(outputPath, tag, ref sidecar);

                DicConsole.WriteLine("Writing metadata sidecar");

                FileStream xmlFs = new FileStream(outputPrefix + ".cicm.xml", FileMode.Create);

                XmlSerializer xmlSer = new XmlSerializer(typeof(CICMMetadataType));
                xmlSer.Serialize(xmlFs, sidecar);
                xmlFs.Close();
            }

            DicConsole.WriteLine();

            DicConsole.WriteLine("Took a total of {0:F3} seconds ({1:F3} processing commands, {2:F3} checksumming, {3:F3} writing, {4:F3} closing).",
                                 (end - start).TotalSeconds, totalDuration / 1000,
                                 totalChkDuration                          / 1000,
                                 imageWriteDuration, (closeEnd - closeStart).TotalSeconds);
            DicConsole.WriteLine("Avegare speed: {0:F3} MiB/sec.",
                                 (double)blockSize * (double)(blocks + 1) / 1048576 / (totalDuration / 1000));
            DicConsole.WriteLine("Fastest speed burst: {0:F3} MiB/sec.", maxSpeed);
            DicConsole.WriteLine("Slowest speed burst: {0:F3} MiB/sec.", minSpeed);
            DicConsole.WriteLine("{0} sectors could not be read.",       resume.BadBlocks.Count);
            DicConsole.WriteLine();

            Statistics.AddMedia(dskType, true);
        }
    }
}