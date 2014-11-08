﻿#region License
/*
Copyright © Joan Charmant 2012.
joan.charmant@gmail.com 
 
This file is part of Kinovea.

Kinovea is free software: you can redistribute it and/or modify
it under the terms of the GNU General Public License version 2 
as published by the Free Software Foundation.

Kinovea is distributed in the hope that it will be useful,
but WITHOUT ANY WARRANTY; without even the implied warranty of
MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
GNU General Public License for more details.

You should have received a copy of the GNU General Public License
along with Kinovea. If not, see http://www.gnu.org/licenses/.
*/
#endregion
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Windows.Forms;
using System.Xml;

using AForge.Video.DirectShow;
using Kinovea.Services;

namespace Kinovea.Camera.DirectShow
{
    /// <summary>
    /// Class to discover and manage cameras connected through DirectShow.
    /// </summary>
    public class CameraManagerDirectShow : CameraManager
    {
        #region Properties
        public override string CameraType 
        { 
            get { return "4602B70E-8FDD-47FF-B012-7C38BB2A16B9";}
        }
        public override string CameraTypeFriendlyName 
        { 
            get { return "DirectShow"; }
        }
        public override bool HasConnectionWizard
        {
            get { return false;}
        }
        #endregion
    
        #region Members
        private static readonly log4net.ILog log = log4net.LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);
        private Dictionary<string, CameraSummary> cache = new Dictionary<string, CameraSummary>();
        private List<string> snapshotting = new List<string>();
        private Bitmap defaultIcon;
        #endregion
        
        public CameraManagerDirectShow()
        {
            defaultIcon = IconLibrary.GetIcon("webcam");
        }

        public override bool SanityCheck()
        {
            return true;
        }
        
        public override List<CameraSummary> DiscoverCameras(IEnumerable<CameraBlurb> blurbs)
        {
            // DirectShow has active discovery. We just ask for the list of cameras connected to the PC.
            List<CameraSummary> summaries = new List<CameraSummary>();
            List<CameraSummary> found = new List<CameraSummary>();
            
            FilterInfoCollection cameras = new FilterInfoCollection(FilterCategory.VideoInputDevice);
            
            foreach(FilterInfo camera in cameras)
            {
                if(BypassCamera(camera))
                    continue;

                // For now consider that the moniker string is like a serial number.
                // Apparently this is only true for certain models.
                // Check if we should extract the serial number part so that we don't change id when changing USB port.
                string identifier = camera.MonikerString;
                bool cached = cache.ContainsKey(identifier);
                
                string alias = camera.Name;
                Bitmap icon = null;
                SpecificInfo specific = null;
                Rectangle displayRectangle = Rectangle.Empty;
                CaptureAspectRatio aspectRatio = CaptureAspectRatio.Auto;
                
                if(blurbs != null)
                {
                    foreach(CameraBlurb blurb in blurbs)
                    {
                        if(blurb.CameraType != this.CameraType || blurb.Identifier != identifier)
                            continue;
                            
                        alias = blurb.Alias;
                        icon = blurb.Icon ?? defaultIcon;
                        displayRectangle = blurb.DisplayRectangle;
                        if(!string.IsNullOrEmpty(blurb.AspectRatio))
                            aspectRatio = (CaptureAspectRatio)Enum.Parse(typeof(CaptureAspectRatio), blurb.AspectRatio);
                        specific = SpecificInfoDeserialize(blurb.Specific);
                        break;
                    }
                }
                
                if(icon == null)
                    icon = defaultIcon;
                
                CameraSummary summary = new CameraSummary(alias, camera.Name, identifier, icon, displayRectangle, aspectRatio, specific, this);
                summaries.Add(summary);
                
                if(cached)
                    found.Add(cache[identifier]);
                    
                if(!cached)
                {
                    cache.Add(identifier, summary);
                    found.Add(summary);
                }
            }
            
            // TODO: do we need to do all this. Just replace the cache with the current list.
            
            List<CameraSummary> lost = new List<CameraSummary>();
            foreach(CameraSummary summary in cache.Values)
            {
                if(!found.Contains(summary))
                   lost.Add(summary);
            }
            
            foreach(CameraSummary summary in lost)
                cache.Remove(summary.Identifier);

            return summaries;
        }
        
        public override void GetSingleImage(CameraSummary summary)
        {
            if(snapshotting.IndexOf(summary.Identifier) >= 0)
                return;
            
            // TODO: Retrieve moniker from identifier.
            string moniker = summary.Identifier;
            
            // Spawn a thread to get a snapshot.
            SnapshotRetriever retriever = new SnapshotRetriever(summary, moniker);
            retriever.CameraImageReceived += SnapshotRetriever_CameraImageReceived;
            snapshotting.Add(summary.Identifier);
            ThreadPool.QueueUserWorkItem(retriever.Run);
        }
        
        public override CameraBlurb BlurbFromSummary(CameraSummary summary)
        {
            string specific = SpecificInfoSerialize(summary);
            CameraBlurb blurb = new CameraBlurb(CameraType, summary.Identifier, summary.Alias, summary.Icon, summary.DisplayRectangle, summary.AspectRatio.ToString(), specific);
            return blurb;
        }
        
        public override IFrameGrabber Connect(CameraSummary summary)
        {
            // TODO: Retrieve moniker from identifier.
            string moniker = summary.Identifier;
            
            FrameGrabber grabber = new FrameGrabber(summary, moniker);
            return grabber;
        }
        
        public override bool Configure(CameraSummary summary)
        {
            bool needsReconnection = false;
            FormConfiguration form = new FormConfiguration(summary);
            if(form.ShowDialog() == DialogResult.OK)
            {
                if(form.AliasChanged)
                    summary.UpdateAlias(form.Alias, form.PickedIcon);
                
                if(form.SpecificChanged)
                {
                    SpecificInfo info = new SpecificInfo();
                    if (form.SelectedMediaType != null)
                    {
                        info.MediaType = form.SelectedMediaType;
                        summary.UpdateSpecific(info);
                    }
                    
                    summary.UpdateDisplayRectangle(Rectangle.Empty);
                    needsReconnection = true;
                }
                
                CameraTypeManager.UpdatedCameraSummary(summary);
            }
            
            form.Dispose();
            return needsReconnection;
        }
        
        public override string GetSummaryAsText(CameraSummary summary)
        {
            string result = "";
            string alias = summary.Alias;
            
            SpecificInfo info = summary.Specific as SpecificInfo;
            if(info != null && info.MediaType != null)
            {
                Size size = info.MediaType.FrameSize;
                float fps = (float)info.MediaType.SelectedFramerate;
                string compression = info.MediaType.Compression;
                result = string.Format("{0} - {1}×{2} @ {3}fps in {4}", alias, size.Width, size.Height, fps, compression);
            }
            else
            {
                result = string.Format("{0}", alias);
            }
            
            return result;
        }
        
        public override Control GetConnectionWizard()
        {
            throw new NotImplementedException();
        }
        
        private void SnapshotRetriever_CameraImageReceived(object sender, CameraImageReceivedEventArgs e)
        {
            SnapshotRetriever retriever = sender as SnapshotRetriever;
            if(retriever != null)
            {
                retriever.CameraImageReceived -= SnapshotRetriever_CameraImageReceived;
                snapshotting.Remove(retriever.Identifier);
            }
            
            OnCameraImageReceived(e);
        }
        
        private bool BypassCamera(FilterInfo camera)
        {
            // Bypass DirectShow filters for industrial camera when we have SDK access.
            return false; // camera.Name == "Basler GenICam Source";
        }
        
        private SpecificInfo SpecificInfoDeserialize(string xml)
        {
            if(string.IsNullOrEmpty(xml))
                return null;
            
            SpecificInfo info = null;
            
            try
            {
                XmlDocument doc = new XmlDocument();
                doc.Load(new StringReader(xml));

                info = new SpecificInfo();

                string compression = null;
                Size frameSize = Size.Empty;
                int selectedFramerate = 0;
                int index = -1;
                int bpp = 0;

                XmlNode xmlCompression = doc.SelectSingleNode("/DirectShow/Compression");
                if (xmlCompression != null)
                    compression = xmlCompression.InnerText;

                XmlNode xmlFrameSize = doc.SelectSingleNode("/DirectShow/FrameSize");
                if(xmlFrameSize != null)
                    frameSize = XmlHelper.ParseSize(xmlFrameSize.InnerText);

                XmlNode xmlSelectedFrameRate = doc.SelectSingleNode("/DirectShow/SelectedFramerate");
                if (xmlSelectedFrameRate != null)
                    selectedFramerate = int.Parse(xmlSelectedFrameRate.InnerText, CultureInfo.InvariantCulture);

                XmlNode xmlIndex = doc.SelectSingleNode("/DirectShow/MediaTypeIndex");
                if (xmlIndex != null)
                    index = int.Parse(xmlIndex.InnerText, CultureInfo.InvariantCulture);

                XmlNode xmlBPP = doc.SelectSingleNode("/DirectShow/BitsPerPixel");
                if (xmlBPP != null)
                    bpp = int.Parse(xmlBPP.InnerText, CultureInfo.InvariantCulture);

                if (!string.IsNullOrEmpty(compression) && frameSize != Size.Empty && selectedFramerate > 0 && index > 0 && bpp > 0)
                    info.MediaType = new MediaType(compression, frameSize, selectedFramerate, index, bpp, null);
            }
            catch(Exception e)
            {
                log.ErrorFormat(e.Message);
            }
            
            return info;
        }
        
        private string SpecificInfoSerialize(CameraSummary summary)
        {
            SpecificInfo info = summary.Specific as SpecificInfo;
            if(info == null)
                return null;
                
            XmlDocument doc = new XmlDocument();
            XmlElement xmlRoot = doc.CreateElement("DirectShow");

            if (info.MediaType == null)
            {
                doc.AppendChild(xmlRoot);
                return doc.OuterXml;
            }

            XmlElement xmlCompression = doc.CreateElement("Compression");
            xmlCompression.InnerText = info.MediaType.Compression;
            xmlRoot.AppendChild(xmlCompression);
            
            XmlElement xmlFrameSize = doc.CreateElement("FrameSize");
            xmlFrameSize.InnerText = string.Format("{0};{1}", info.MediaType.FrameSize.Width, info.MediaType.FrameSize.Height);
            xmlRoot.AppendChild(xmlFrameSize);

            XmlElement xmlFramerate = doc.CreateElement("SelectedFramerate");
            xmlFramerate.InnerText = string.Format("{0}", info.MediaType.SelectedFramerate);
            xmlRoot.AppendChild(xmlFramerate);

            XmlElement xmlIndex = doc.CreateElement("MediaTypeIndex");
            xmlIndex.InnerText = string.Format("{0}", info.MediaType.MediaTypeIndex);
            xmlRoot.AppendChild(xmlIndex);

            XmlElement xmlBPP = doc.CreateElement("BitsPerPixel");
            xmlBPP.InnerText = string.Format("{0}", info.MediaType.BitsPerPixel);
            xmlRoot.AppendChild(xmlBPP);

            doc.AppendChild(xmlRoot);
            
            return doc.OuterXml;
        }
    }
}