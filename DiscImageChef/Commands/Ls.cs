// /***************************************************************************
// The Disc Image Chef
// ----------------------------------------------------------------------------
//
// Filename       : Ls.cs
// Author(s)      : Natalia Portillo <claunia@claunia.com>
//
// Component      : Verbs.
//
// --[ Description ] ----------------------------------------------------------
//
//     Implements the 'ls' verb.
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
using System.Text;
using DiscImageChef.CommonTypes;
using DiscImageChef.Console;
using DiscImageChef.Core;
using DiscImageChef.DiscImages;
using DiscImageChef.Filesystems;
using DiscImageChef.Filters;

namespace DiscImageChef.Commands
{
    static class Ls
    {
        internal static void DoLs(LsOptions options)
        {
            DicConsole.DebugWriteLine("Ls command", "--debug={0}",   options.Debug);
            DicConsole.DebugWriteLine("Ls command", "--verbose={0}", options.Verbose);
            DicConsole.DebugWriteLine("Ls command", "--input={0}",   options.InputFile);

            FiltersList                filtersList = new FiltersList();
            IFilter                    inputFilter = filtersList.GetFilter(options.InputFile);
            
            Dictionary<string, string> parsedOptions = Options.Parse(options.Options);
            DicConsole.DebugWriteLine("Ls command", "Parsed options:");
            foreach(KeyValuePair<string,string> parsedOption in parsedOptions)
                DicConsole.DebugWriteLine("Ls command", "{0} = {1}", parsedOption.Key, parsedOption.Value);
            parsedOptions.Add("debug", options.Debug.ToString());

            if(inputFilter == null)
            {
                DicConsole.ErrorWriteLine("Cannot open specified file.");
                return;
            }

            Encoding encoding = null;

            if(options.EncodingName != null)
                try
                {
                    encoding = Claunia.Encoding.Encoding.GetEncoding(options.EncodingName);
                    if(options.Verbose) DicConsole.VerboseWriteLine("Using encoding for {0}.", encoding.EncodingName);
                }
                catch(ArgumentException)
                {
                    DicConsole.ErrorWriteLine("Specified encoding is not supported.");
                    return;
                }

            PluginBase plugins = new PluginBase();

            try
            {
                IMediaImage imageFormat = ImageFormat.Detect(inputFilter);

                if(imageFormat == null)
                {
                    DicConsole.WriteLine("Image format not identified, not proceeding with analysis.");
                    return;
                }

                if(options.Verbose)
                    DicConsole.VerboseWriteLine("Image format identified by {0} ({1}).", imageFormat.Name,
                                                imageFormat.Id);
                else DicConsole.WriteLine("Image format identified by {0}.", imageFormat.Name);

                try
                {
                    if(!imageFormat.Open(inputFilter))
                    {
                        DicConsole.WriteLine("Unable to open image format");
                        DicConsole.WriteLine("No error given");
                        return;
                    }

                    DicConsole.DebugWriteLine("Ls command", "Correctly opened image file.");
                    DicConsole.DebugWriteLine("Ls command", "Image without headers is {0} bytes.",
                                              imageFormat.Info.ImageSize);
                    DicConsole.DebugWriteLine("Ls command", "Image has {0} sectors.", imageFormat.Info.Sectors);
                    DicConsole.DebugWriteLine("Ls command", "Image identifies disk type as {0}.",
                                              imageFormat.Info.MediaType);

                    Core.Statistics.AddMediaFormat(imageFormat.Format);
                    Core.Statistics.AddMedia(imageFormat.Info.MediaType, false);
                    Core.Statistics.AddFilter(inputFilter.Name);
                }
                catch(Exception ex)
                {
                    DicConsole.ErrorWriteLine("Unable to open image format");
                    DicConsole.ErrorWriteLine("Error: {0}", ex.Message);
                    return;
                }

                List<Partition> partitions = Core.Partitions.GetAll(imageFormat);
                Core.Partitions.AddSchemesToStats(partitions);

                List<string>        idPlugins;
                IReadOnlyFilesystem plugin;
                Errno               error;
                if(partitions.Count == 0) DicConsole.DebugWriteLine("Ls command", "No partitions found");
                else
                {
                    DicConsole.WriteLine("{0} partitions found.", partitions.Count);

                    for(int i = 0; i < partitions.Count; i++)
                    {
                        DicConsole.WriteLine();
                        DicConsole.WriteLine("Partition {0}:", partitions[i].Sequence);

                        DicConsole.WriteLine("Identifying filesystem on partition");

                        Core.Filesystems.Identify(imageFormat, out idPlugins, partitions[i]);
                        if(idPlugins.Count      == 0) DicConsole.WriteLine("Filesystem not identified");
                        else if(idPlugins.Count > 1)
                        {
                            DicConsole.WriteLine($"Identified by {idPlugins.Count} plugins");

                            foreach(string pluginName in idPlugins)
                                if(plugins.ReadOnlyFilesystems.TryGetValue(pluginName, out plugin))
                                {
                                    DicConsole.WriteLine($"As identified by {plugin.Name}.");
                                    IReadOnlyFilesystem fs = (IReadOnlyFilesystem)plugin
                                                                                 .GetType()
                                                                                 .GetConstructor(Type.EmptyTypes)
                                                                                ?.Invoke(new object[] { });

                                    if(fs == null) continue;

                                    error = fs.Mount(imageFormat, partitions[i], encoding, parsedOptions);
                                    if(error == Errno.NoError)
                                    {
                                        List<string> rootDir = new List<string>();
                                        error                = fs.ReadDir("/", out rootDir);
                                        if(error == Errno.NoError)
                                            foreach(string entry in rootDir)
                                                DicConsole.WriteLine("{0}", entry);
                                        else
                                            DicConsole.ErrorWriteLine("Error {0} reading root directory {0}",
                                                                      error.ToString());

                                        Core.Statistics.AddFilesystem(fs.XmlFsType.Type);
                                    }
                                    else
                                        DicConsole.ErrorWriteLine("Unable to mount device, error {0}",
                                                                  error.ToString());
                                }
                        }
                        else
                        {
                            plugins.ReadOnlyFilesystems.TryGetValue(idPlugins[0], out plugin);
                            if(plugin == null) continue;

                            DicConsole.WriteLine($"Identified by {plugin.Name}.");
                            IReadOnlyFilesystem fs = (IReadOnlyFilesystem)plugin
                                                                         .GetType().GetConstructor(Type.EmptyTypes)
                                                                        ?.Invoke(new object[] { });
                            if(fs == null) continue;

                            error = fs.Mount(imageFormat, partitions[i], encoding, parsedOptions);
                            if(error == Errno.NoError)
                            {
                                List<string> rootDir = new List<string>();
                                error                = fs.ReadDir("/", out rootDir);
                                if(error == Errno.NoError)
                                    foreach(string entry in rootDir)
                                        DicConsole.WriteLine("{0}", entry);
                                else
                                    DicConsole.ErrorWriteLine("Error {0} reading root directory {0}", error.ToString());

                                Core.Statistics.AddFilesystem(fs.XmlFsType.Type);
                            }
                            else DicConsole.ErrorWriteLine("Unable to mount device, error {0}", error.ToString());
                        }
                    }
                }

                Partition wholePart = new Partition
                {
                    Name   = "Whole device",
                    Length = imageFormat.Info.Sectors,
                    Size   = imageFormat.Info.Sectors * imageFormat.Info.SectorSize
                };

                Core.Filesystems.Identify(imageFormat, out idPlugins, wholePart);
                if(idPlugins.Count      == 0) DicConsole.WriteLine("Filesystem not identified");
                else if(idPlugins.Count > 1)
                {
                    DicConsole.WriteLine($"Identified by {idPlugins.Count} plugins");

                    foreach(string pluginName in idPlugins)
                        if(plugins.ReadOnlyFilesystems.TryGetValue(pluginName, out plugin))
                        {
                            DicConsole.WriteLine($"As identified by {plugin.Name}.");
                            IReadOnlyFilesystem fs = (IReadOnlyFilesystem)plugin
                                                                         .GetType().GetConstructor(Type.EmptyTypes)
                                                                        ?.Invoke(new object[] { });
                            if(fs == null) continue;

                            error = fs.Mount(imageFormat, wholePart, encoding, parsedOptions);
                            if(error == Errno.NoError)
                            {
                                List<string> rootDir = new List<string>();
                                error                = fs.ReadDir("/", out rootDir);
                                if(error == Errno.NoError)
                                    foreach(string entry in rootDir)
                                        DicConsole.WriteLine("{0}", entry);
                                else
                                    DicConsole.ErrorWriteLine("Error {0} reading root directory {0}", error.ToString());

                                Core.Statistics.AddFilesystem(fs.XmlFsType.Type);
                            }
                            else DicConsole.ErrorWriteLine("Unable to mount device, error {0}", error.ToString());
                        }
                }
                else
                {
                    plugins.ReadOnlyFilesystems.TryGetValue(idPlugins[0], out plugin);
                    if(plugin != null)
                    {
                        DicConsole.WriteLine($"Identified by {plugin.Name}.");
                        IReadOnlyFilesystem fs =
                            (IReadOnlyFilesystem)plugin
                                                .GetType().GetConstructor(Type.EmptyTypes)?.Invoke(new object[] { });
                        if(fs != null)
                        {
                            error = fs.Mount(imageFormat, wholePart, encoding, parsedOptions);
                            if(error == Errno.NoError)
                            {
                                List<string> rootDir = new List<string>();
                                error                = fs.ReadDir("/", out rootDir);
                                if(error == Errno.NoError)
                                    foreach(string entry in rootDir)
                                        if(options.Long)
                                        {
                                            FileEntryInfo stat   = new FileEntryInfo();
                                            List<string>  xattrs = new List<string>();

                                            error = fs.Stat(entry, out stat);
                                            if(error == Errno.NoError)
                                            {
                                                DicConsole.WriteLine("{0}\t{1}\t{2} bytes\t{3}", stat.CreationTimeUtc,
                                                                     stat.Inode, stat.Length, entry);

                                                error = fs.ListXAttr(entry, out xattrs);
                                                if(error != Errno.NoError) continue;

                                                foreach(string xattr in xattrs)
                                                {
                                                    byte[] xattrBuf = new byte[0];
                                                    error           = fs.GetXattr(entry, xattr, ref xattrBuf);
                                                    if(error == Errno.NoError)
                                                        DicConsole.WriteLine("\t\t{0}\t{1} bytes", xattr,
                                                                             xattrBuf.Length);
                                                }
                                            }
                                            else DicConsole.WriteLine("{0}", entry);
                                        }
                                        else
                                            DicConsole.WriteLine("{0}", entry);
                                else
                                    DicConsole.ErrorWriteLine("Error {0} reading root directory {0}", error.ToString());

                                Core.Statistics.AddFilesystem(fs.XmlFsType.Type);
                            }
                            else DicConsole.ErrorWriteLine("Unable to mount device, error {0}", error.ToString());
                        }
                    }
                }
            }
            catch(Exception ex)
            {
                DicConsole.ErrorWriteLine($"Error reading file: {ex.Message}");
                DicConsole.DebugWriteLine("Ls command", ex.StackTrace);
            }

            Core.Statistics.AddCommand("ls");
        }
    }
}