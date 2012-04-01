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
using System.Xml;

using Kinovea.Services;

namespace Kinovea.ScreenManager
{
    /// <summary>
    /// Support class for custom drawings.
    /// The class takes the drawing shape and behavior from an XML file.
    /// </summary>
    public class GenericPosture
    {
        #region Properties
        public List<Point> Points { get; private set; }
        public List<GenericPostureSegment> Segments { get; private set;}
        public List<GenericPostureAngle> Angles { get; private set;}
        public List<GenericPostureHandle> Handles { get; private set; }
        #endregion
        
        #region Members
        private static readonly log4net.ILog log = log4net.LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);
        #endregion
        
        #region Constructor
        public GenericPosture(string descriptionFile)
        {
            Points = new List<Point>();
            Segments = new List<GenericPostureSegment>();
            Handles = new List<GenericPostureHandle>();
            Angles = new List<GenericPostureAngle>();
            
            XmlReaderSettings settings = new XmlReaderSettings();
            settings.IgnoreComments = true;
            settings.IgnoreProcessingInstructions = true;
            settings.IgnoreWhitespace = true;
            settings.CloseInput = true;

            XmlReader reader = XmlReader.Create(descriptionFile, settings);
            ReadXml(reader);
            reader.Close();
        }
        #endregion
        
        #region Serialization - Reading
        private void ReadXml(XmlReader r)
        {
            r.MoveToContent();
            
            if(!(r.Name == "KinoveaPostureTool"))
        	    return;
            
        	r.ReadStartElement();
        	r.ReadElementContentAsString("FormatVersion", "");
        	
        	while(r.NodeType == XmlNodeType.Element)
			{
                switch(r.Name)
				{
                    case "PointCount":
                        ParsePointCount(r);
						break;
                    case "Segments":
						ParseSegments(r);
						break;
					case "Angles":
						ParseAngles(r);
						break;
					case "Handles":
						ParseHandles(r);
						break;
					case "InitialConfiguration":
						ParseInitialConfiguration(r);
						break;
                    default:
						string unparsed = r.ReadOuterXml();
						log.DebugFormat("Unparsed content in XML: {0}", unparsed);
						break;
                }
            }
            
            r.ReadEndElement();
        }
        private void ParsePointCount(XmlReader r)
        {
            int pointCount = r.ReadElementContentAsInt();
            for(int i=0;i<pointCount;i++)
                Points.Add(Point.Empty);
        }
        private void ParseSegments(XmlReader r)
        {
            r.ReadStartElement();
            
            while(r.NodeType == XmlNodeType.Element)
            {
                if(r.Name == "Segment")
                {
                    Segments.Add(new GenericPostureSegment(r));
                }
                else
                {
                    string outerXml = r.ReadOuterXml();
                    log.DebugFormat("Unparsed content in XML: {0}", outerXml);
                }
            }
            
            r.ReadEndElement();
        }
        private void ParseAngles(XmlReader r)
        {
            r.ReadStartElement();
            
            while(r.NodeType == XmlNodeType.Element)
            {
                if(r.Name == "Angle")
                {
                    Angles.Add(new GenericPostureAngle(r));
                }
                else
                {
                    string outerXml = r.ReadOuterXml();
                    log.DebugFormat("Unparsed content in XML: {0}", outerXml);
                }
            }
            
            r.ReadEndElement();
        }
        private void ParseHandles(XmlReader r)
        {
            r.ReadStartElement();
            
            while(r.NodeType == XmlNodeType.Element)
            {
                if(r.Name == "Handle")
                {
                    Handles.Add(new GenericPostureHandle(r));
                }
                else
                {
                    string outerXml = r.ReadOuterXml();
                    log.DebugFormat("Unparsed content in XML: {0}", outerXml);
                }
            }
            
            r.ReadEndElement();
        }
        private void ParseInitialConfiguration(XmlReader r)
        {
            r.ReadStartElement();
            int index = 0;
            while(r.NodeType == XmlNodeType.Element)
            {
                if(r.Name == "Point")
                {
                    if(index < Points.Count)
                    {
                        Points[index] = XmlHelper.ParsePoint(r.ReadElementContentAsString());
                        index++;
                    }
                    else
                    {
                        string outerXml = r.ReadOuterXml();
                        log.DebugFormat("Unparsed point in initial configuration: {0}", outerXml);
                    }
                }
                else
                {
                    string outerXml = r.ReadOuterXml();
                    log.DebugFormat("Unparsed content: {0}", outerXml);
                }
            }
            
            r.ReadEndElement();
        }
        #endregion
        
        
        
    }
}
