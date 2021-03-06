// /***************************************************************************
// The Disc Image Chef
// ----------------------------------------------------------------------------
//
// Filename       : Statistics.aspx.cs
// Author(s)      : Natalia Portillo <claunia@claunia.com>
//
// Component      : DiscImageChef Server.
//
// --[ Description ] ----------------------------------------------------------
//
//     Renders statistics and links to reports.
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
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Web;
using System.Web.Hosting;
using System.Web.UI;
using System.Xml.Serialization;
using DiscImageChef.Interop;
using DiscImageChef.Metadata;
using PlatformID = DiscImageChef.Interop.PlatformID;

namespace DiscImageChef.Server
{
    /// <summary>
    ///     Renders a page with statistics, list of media type, devices, etc
    /// </summary>
    public partial class Statistics : Page
    {
        List<DeviceItem>     devices;
        List<NameValueStats> operatingSystems;
        List<MediaItem>      realMedia;

        Stats                statistics;
        List<NameValueStats> versions;
        List<MediaItem>      virtualMedia;

        protected void Page_Load(object sender, EventArgs e)
        {
            lblVersion.Text = Assembly.GetExecutingAssembly().GetName().Version.ToString();

            try
            {
                if(!File.Exists(Path.Combine(HostingEnvironment.MapPath("~") ?? throw new InvalidOperationException(),
                                             "Statistics", "Statistics.xml")))
                {
                    #if DEBUG
                    content.InnerHtml =
                        $"<b>Sorry, cannot load data file \"{Path.Combine(HostingEnvironment.MapPath("~") ?? throw new InvalidOperationException(), "Statistics", "Statistics.xml")}\"</b>";
                    #else
                    content.InnerHtml = "<b>Sorry, cannot load data file</b>";
                    #endif
                    return;
                }

                statistics = new Stats();

                XmlSerializer xs = new XmlSerializer(statistics.GetType());
                FileStream    fs =
                    WaitForFile(Path.Combine(HostingEnvironment.MapPath("~") ?? throw new InvalidOperationException(), "Statistics", "Statistics.xml"),
                                FileMode.Open, FileAccess.Read, FileShare.Read);
                statistics = (Stats)xs.Deserialize(fs);
                fs.Close();

                if(statistics.OperatingSystems != null)
                {
                    operatingSystems = new List<NameValueStats>();
                    foreach(OsStats nvs in statistics.OperatingSystems)
                        operatingSystems.Add(new NameValueStats
                        {
                            name =
                                $"{DetectOS.GetPlatformName((PlatformID)Enum.Parse(typeof(PlatformID), nvs.name), nvs.version)}{(string.IsNullOrEmpty(nvs.version) ? "" : " ")}{nvs.version}",
                            Value = nvs.Value
                        });

                    repOperatingSystems.DataSource = operatingSystems.OrderBy(os => os.name).ToList();
                    repOperatingSystems.DataBind();
                }
                else divOperatingSystems.Visible = false;

                if(statistics.Versions != null)
                {
                    versions = new List<NameValueStats>();
                    foreach(NameValueStats nvs in statistics.Versions)
                        versions.Add(nvs.name == "previous"
                                         ? new NameValueStats {name = "Previous than 3.4.99.0", Value = nvs.Value}
                                         : nvs);

                    repVersions.DataSource = versions.OrderBy(ver => ver.name).ToList();
                    repVersions.DataBind();
                }
                else divVersions.Visible = false;

                if(statistics.Commands != null)
                {
                    lblAnalyze.Text       = statistics.Commands.Analyze.ToString();
                    lblCompare.Text       = statistics.Commands.Compare.ToString();
                    lblChecksum.Text      = statistics.Commands.Checksum.ToString();
                    lblEntropy.Text       = statistics.Commands.Entropy.ToString();
                    lblVerify.Text        = statistics.Commands.Verify.ToString();
                    lblPrintHex.Text      = statistics.Commands.PrintHex.ToString();
                    lblDecode.Text        = statistics.Commands.Decode.ToString();
                    lblDeviceInfo.Text    = statistics.Commands.DeviceInfo.ToString();
                    lblMediaInfo.Text     = statistics.Commands.MediaInfo.ToString();
                    lblMediaScan.Text     = statistics.Commands.MediaScan.ToString();
                    lblFormats.Text       = statistics.Commands.Formats.ToString();
                    lblBenchmark.Text     = statistics.Commands.Benchmark.ToString();
                    lblCreateSidecar.Text = statistics.Commands.CreateSidecar.ToString();
                    lblDumpMedia.Text     = statistics.Commands.DumpMedia.ToString();
                    lblDeviceReport.Text  = statistics.Commands.DeviceReport.ToString();
                    lblLs.Text            = statistics.Commands.Ls.ToString();
                    lblExtractFiles.Text  = statistics.Commands.ExtractFiles.ToString();
                    lblListDevices.Text   = statistics.Commands.ListDevices.ToString();
                    lblListEncodings.Text = statistics.Commands.ListEncodings.ToString();
                    lblConvertImage.Text  = statistics.Commands.ConvertImage.ToString();
                    lblImageInfo.Text     = statistics.Commands.ImageInfo.ToString();
                }
                else divCommands.Visible = false;

                if(statistics.Filters != null)
                {
                    repFilters.DataSource = statistics.Filters.OrderBy(filter => filter.name).ToList();
                    repFilters.DataBind();
                }
                else divFilters.Visible = false;

                if(statistics.MediaImages != null)
                {
                    repMediaImages.DataSource = statistics.MediaImages.OrderBy(filter => filter.name).ToList();
                    repMediaImages.DataBind();
                }
                else divMediaImages.Visible = false;

                if(statistics.Partitions != null)
                {
                    repPartitions.DataSource = statistics.Partitions.OrderBy(filter => filter.name).ToList();
                    repPartitions.DataBind();
                }
                else divPartitions.Visible = false;

                if(statistics.Filesystems != null)
                {
                    repFilesystems.DataSource = statistics.Filesystems.OrderBy(filter => filter.name).ToList();
                    repFilesystems.DataBind();
                }
                else divFilesystems.Visible = false;

                if(statistics.Medias != null)
                {
                    realMedia    = new List<MediaItem>();
                    virtualMedia = new List<MediaItem>();
                    foreach(MediaStats nvs in statistics.Medias)
                    {
                        MediaType
                           .MediaTypeToString((CommonTypes.MediaType)Enum.Parse(typeof(CommonTypes.MediaType), nvs.type),
                                              out string type, out string subtype);

                        if(nvs.real) realMedia.Add(new MediaItem {Type = type, SubType = subtype, Count = nvs.Value});
                        else virtualMedia.Add(new MediaItem {Type      = type, SubType = subtype, Count = nvs.Value});
                    }

                    if(realMedia.Count > 0)
                    {
                        repRealMedia.DataSource =
                            realMedia.OrderBy(media => media.Type).ThenBy(media => media.SubType).ToList();
                        repRealMedia.DataBind();
                    }
                    else divRealMedia.Visible = false;

                    if(virtualMedia.Count > 0)
                    {
                        repVirtualMedia.DataSource =
                            virtualMedia.OrderBy(media => media.Type).ThenBy(media => media.SubType).ToList();
                        repVirtualMedia.DataBind();
                    }
                    else divVirtualMedia.Visible = false;
                }
                else
                {
                    divRealMedia.Visible    = false;
                    divVirtualMedia.Visible = false;
                }

                if(statistics.Devices != null)
                {
                    devices = new List<DeviceItem>();
                    foreach(DeviceStats device in statistics.Devices)
                    {
                        string url;
                        string xmlFile;
                        if(!string.IsNullOrWhiteSpace(device.Manufacturer) &&
                           !string.IsNullOrWhiteSpace(device.Model)        &&
                           !string.IsNullOrWhiteSpace(device.Revision))
                        {
                            xmlFile = device.Manufacturer + "_" + device.Model + "_" + device.Revision + ".xml";
                            url     =
                                $"ViewReport.aspx?manufacturer={HttpUtility.UrlPathEncode(device.Manufacturer)}&model={HttpUtility.UrlPathEncode(device.Model)}&revision={HttpUtility.UrlPathEncode(device.Revision)}";
                        }
                        else if(!string.IsNullOrWhiteSpace(device.Manufacturer) &&
                                !string.IsNullOrWhiteSpace(device.Model))
                        {
                            xmlFile = device.Manufacturer + "_" + device.Model + ".xml";
                            url     =
                                $"ViewReport.aspx?manufacturer={HttpUtility.UrlPathEncode(device.Manufacturer)}&model={HttpUtility.UrlPathEncode(device.Model)}";
                        }
                        else if(!string.IsNullOrWhiteSpace(device.Model) && !string.IsNullOrWhiteSpace(device.Revision))
                        {
                            xmlFile = device.Model + "_" + device.Revision + ".xml";
                            url     =
                                $"ViewReport.aspx?model={HttpUtility.UrlPathEncode(device.Model)}&revision={HttpUtility.UrlPathEncode(device.Revision)}";
                        }
                        else
                        {
                            xmlFile = device.Model + ".xml";
                            url     = $"ViewReport.aspx?model={HttpUtility.UrlPathEncode(device.Model)}";
                        }

                        xmlFile = xmlFile.Replace('/', '_').Replace('\\', '_').Replace('?', '_');

                        if(!File.Exists(Path.Combine(HostingEnvironment.MapPath("~"), "Reports", xmlFile))) url = null;

                        devices.Add(new DeviceItem
                        {
                            Manufacturer = device.Manufacturer,
                            Model        = device.Model,
                            Revision     = device.Revision,
                            Bus          = device.Bus,
                            ReportLink   = url == null ? "No" : $"<a href=\"{url}\" target=\"_blank\">Yes</a>"
                        });
                    }

                    repDevices.DataSource = devices.OrderBy(device => device.Manufacturer)
                                                   .ThenBy(device => device.Model).ThenBy(device => device.Revision)
                                                   .ThenBy(device => device.Bus).ToList();
                    repDevices.DataBind();
                }
                else divDevices.Visible = false;
            }
            catch(Exception)
            {
                content.InnerHtml = "<b>Could not load statistics</b>";
                #if DEBUG
                throw;
                #endif
            }
        }

        static FileStream WaitForFile(string fullPath, FileMode mode, FileAccess access, FileShare share)
        {
            for(int numTries = 0; numTries < 100; numTries++)
            {
                FileStream fs = null;
                try
                {
                    fs = new FileStream(fullPath, mode, access, share);
                    return fs;
                }
                catch(IOException)
                {
                    fs?.Dispose();
                    Thread.Sleep(50);
                }
            }

            return null;
        }

        class MediaItem
        {
            public string Type    { get; set; }
            public string SubType { get; set; }
            public long   Count   { get; set; }
        }

        class DeviceItem
        {
            public string Manufacturer { get; set; }
            public string Model        { get; set; }
            public string Revision     { get; set; }
            public string Bus          { get; set; }
            public string ReportLink   { get; set; }
        }
    }
}