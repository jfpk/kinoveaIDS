
/*
Copyright � Joan Charmant 2008.
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

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Reflection;
using System.Resources;
using System.Threading;
using System.Windows.Forms;
using System.Xml;
using System.Xml.Serialization;

using Kinovea.ScreenManager.Languages;
using Kinovea.Services;

namespace Kinovea.ScreenManager
{
    [XmlType ("Angle")]
    public class DrawingAngle : AbstractDrawing, IKvaSerializable, IDecorable, IInitializable, ITrackable, IMeasurable
    {
        #region Events
        public event EventHandler<TrackablePointMovedEventArgs> TrackablePointMoved;
        public event EventHandler ShowMeasurableInfoChanged;
        #endregion
        
        #region Properties
        public override string ToolDisplayName
        {
            get { return ScreenManagerLang.ToolTip_DrawingToolAngle2D; }
        }
        public override int ContentHash
        {
            get 
            {
                int hash = 0;
                
                // The hash of positions will be taken into account by trackability manager.
                hash ^= styleHelper.ContentHash;
                hash ^= infosFading.ContentHash;
                
                return hash; 
            }
        } 
        public DrawingStyle DrawingStyle
        {
            get { return style;}
        }
        public override InfosFading InfosFading
        {
            get{ return infosFading;}
            set{ infosFading = value;}
        }
        public override DrawingCapabilities Caps
        {
            get { return DrawingCapabilities.ConfigureColor | DrawingCapabilities.Fading | DrawingCapabilities.Track; }
        }
        public override List<ToolStripItem> ContextMenu
        {
            get 
            {
                // Rebuild the menu to get the localized text.
                List<ToolStripItem> contextMenu = new List<ToolStripItem>();
                
                mnuInvertAngle.Text = ScreenManagerLang.mnuInvertAngle;
                contextMenu.Add(mnuInvertAngle);
                
                return contextMenu; 
            }
        }
        public bool Initializing
        {
            get { return initializing; }
        }
        public CalibrationHelper CalibrationHelper { get; set; }
        public bool ShowMeasurableInfo { get; set; }
        #endregion

        #region Members
        private Dictionary<string, PointF> points = new Dictionary<string, PointF>();
        private bool tracking;
        private bool initializing = true;
        
        private AngleHelper angleHelper = new AngleHelper(false, 40, false, "");
        private DrawingStyle style;
        private StyleHelper styleHelper = new StyleHelper();
        private InfosFading infosFading;
        
        private ToolStripMenuItem mnuInvertAngle = new ToolStripMenuItem();
        
        private const int defaultBackgroundAlpha = 92;
        private static readonly log4net.ILog log = log4net.LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);
        #endregion

        #region Constructor
        public DrawingAngle(PointF origin, long timestamp, long averageTimeStampsPerFrame, DrawingStyle preset = null, IImageToViewportTransformer transformer = null)
        {
            int length = 50;
            if (transformer != null)
                length = transformer.Untransform(50);

            points.Add("o", origin);
            points.Add("a", origin.Translate(0, -length));
            points.Add("b", origin.Translate(length, 0));

            styleHelper.Bicolor = new Bicolor(Color.Empty);
            styleHelper.Font = new Font("Arial", 12, FontStyle.Bold);

            if (preset == null)
                preset = ToolManager.GetStylePreset("Angle");

            style = preset.Clone();
            BindStyle();
            
            // Fading
            infosFading = new InfosFading(timestamp, averageTimeStampsPerFrame);

            mnuInvertAngle.Click += mnuInvertAngle_Click;
            mnuInvertAngle.Image = Properties.Drawings.angleinvert;
        }
        public DrawingAngle(XmlReader xmlReader, PointF scale, TimestampMapper timestampMapper, Metadata parent)
            : this(PointF.Empty, 0, 0)
        {
            ReadXml(xmlReader, scale, timestampMapper);
        }
        #endregion

        #region AbstractDrawing Implementation
        public override void Draw(Graphics canvas, DistortionHelper distorter, IImageToViewportTransformer transformer, bool selected, long currentTimestamp)
        {
            double opacityFactor = infosFading.GetOpacityFactor(currentTimestamp);
            
            if(tracking)
                opacityFactor = 1.0;
            
            if (opacityFactor <= 0)
                return;
            
            ComputeValues(transformer);
            
            Point pointO = transformer.Transform(points["o"]);
            Point pointA = transformer.Transform(points["a"]);
            Point pointB = transformer.Transform(points["b"]);
            Rectangle boundingBox = transformer.Transform(angleHelper.BoundingBox);

            if (boundingBox.Size == Size.Empty)
                return;

            using(Pen penEdges = styleHelper.GetBackgroundPen((int)(opacityFactor*255)))
            using(SolidBrush brushEdges = styleHelper.GetBackgroundBrush((int)(opacityFactor*255)))
            using(SolidBrush brushFill = styleHelper.GetBackgroundBrush((int)(opacityFactor*defaultBackgroundAlpha)))
            {
                // Disk section
                canvas.FillPie(brushFill, boundingBox, (float)angleHelper.Angle.Start, (float)angleHelper.Angle.Sweep);
                canvas.DrawPie(penEdges, boundingBox, (float)angleHelper.Angle.Start, (float)angleHelper.Angle.Sweep);
    
                // Edges
                canvas.DrawLine(penEdges, pointO, pointA);
                canvas.DrawLine(penEdges, pointO, pointB);
    
                // Handlers
                canvas.DrawEllipse(penEdges, pointO.Box(3));
                canvas.FillEllipse(brushEdges, pointA.Box(3));
                canvas.FillEllipse(brushEdges, pointB.Box(3));
                
                SolidBrush fontBrush = styleHelper.GetForegroundBrush((int)(opacityFactor * 255));
                float angle = CalibrationHelper.ConvertAngleFromDegrees(angleHelper.CalibratedAngle.Sweep);
                string label = "";
                if (CalibrationHelper.AngleUnit == AngleUnit.Degree)
                    label = string.Format("{0}{1}", (int)Math.Round(angle), CalibrationHelper.GetAngleAbbreviation());
                else
                    label = string.Format("{0:0.00} {1}", angle, CalibrationHelper.GetAngleAbbreviation());

                Font tempFont = styleHelper.GetFont((float)transformer.Scale);
                SizeF labelSize = canvas.MeasureString(label, tempFont);
                
                // Background
                float shiftx = (float)(transformer.Scale * angleHelper.TextPosition.X);
                float shifty = (float)(transformer.Scale * angleHelper.TextPosition.Y);
                PointF textOrigin = new PointF(shiftx + pointO.X - labelSize.Width / 2, shifty + pointO.Y - labelSize.Height / 2);
                RectangleF backRectangle = new RectangleF(textOrigin, labelSize);
                RoundedRectangle.Draw(canvas, backRectangle, brushFill, tempFont.Height/4, false, false, null);
        
                // Text
                canvas.DrawString(label, tempFont, fontBrush, backRectangle.Location);
                
                tempFont.Dispose();
                fontBrush.Dispose();
            }
        }
        public override int HitTest(PointF point, long currentTimestamp, DistortionHelper distorter, IImageToViewportTransformer transformer, bool zooming)
        {
            // Convention: miss = -1, object = 0, handle = n.
            int result = -1;
            
            if (tracking || infosFading.GetOpacityFactor(currentTimestamp) > 0)
            {
                if (HitTester.HitTest(points["o"], point, transformer))
                    result = 1;
                else if (HitTester.HitTest(points["a"], point, transformer))
                    result = 2;
                else if (HitTester.HitTest(points["b"], point, transformer))
                    result = 3;
                else if (IsPointInObject(point))
                    result = 0;
            }
            
            return result;
        }
        public override void MoveHandle(PointF point, int handle, Keys modifiers)
        {
            int constraintAngleSubdivisions = 8; // (Constraint by 45� steps).
            switch (handle)
            {
                case 1:
                    points["o"] = point;
                    SignalTrackablePointMoved("o");
                    break;
                case 2:
                    if((modifiers & Keys.Shift) == Keys.Shift)
                        points["a"] = GeometryHelper.GetPointAtClosestRotationStepCardinal(points["o"], point, constraintAngleSubdivisions);
                    else
                        points["a"] = point;
                    
                    SignalTrackablePointMoved("a");
                    break;
                case 3:
                    if((modifiers & Keys.Shift) == Keys.Shift)
                        points["b"] = GeometryHelper.GetPointAtClosestRotationStepCardinal(points["o"], point, constraintAngleSubdivisions);
                    else
                        points["b"] = point;
                    
                    SignalTrackablePointMoved("b");
                    break;
                default:
                    break;
            }
        }
        public override void MoveDrawing(float dx, float dy, Keys modifierKeys, bool zooming)
        {
            points["o"] = points["o"].Translate(dx, dy);
            points["a"] = points["a"].Translate(dx, dy);
            points["b"] = points["b"].Translate(dx, dy);
            SignalAllTrackablePointsMoved();
        }
        #endregion
            
        #region KVA Serialization
        public void ReadXml(XmlReader xmlReader, PointF scale, TimestampMapper timestampMapper)
        {
            if (xmlReader.MoveToAttribute("id"))
                identifier = new Guid(xmlReader.ReadContentAsString());

            if (xmlReader.MoveToAttribute("name"))
                name = xmlReader.ReadContentAsString();

            xmlReader.ReadStartElement();
            
            while(xmlReader.NodeType == XmlNodeType.Element)
            {
                switch(xmlReader.Name)
                {
                    case "PointO":
                        points["o"] = XmlHelper.ParsePointF(xmlReader.ReadElementContentAsString());
                        break;
                    case "PointA":
                        points["a"] = XmlHelper.ParsePointF(xmlReader.ReadElementContentAsString());
                        break;
                    case "PointB":
                        points["b"] = XmlHelper.ParsePointF(xmlReader.ReadElementContentAsString());
                        break;
                    case "DrawingStyle":
                        style = new DrawingStyle(xmlReader);
                        BindStyle();
                        break;
                    case "InfosFading":
                        infosFading.ReadXml(xmlReader);
                        break;
                    case "Measure":
                        xmlReader.ReadOuterXml();
                        break;
                    default:
                        string unparsed = xmlReader.ReadOuterXml();
                        log.DebugFormat("Unparsed content in KVA XML: {0}", unparsed);
                        break;
                }
            }
            
            xmlReader.ReadEndElement();
            initializing = false;

            points["o"] = points["o"].Scale(scale.X, scale.Y);
            points["a"] = points["a"].Scale(scale.X, scale.Y);
            points["b"] = points["b"].Scale(scale.X, scale.Y);
            SignalAllTrackablePointsMoved();
        }
        public void WriteXml(XmlWriter w, SerializationFilter filter)
        {
            if (ShouldSerializeCore(filter))
            {
                w.WriteElementString("PointO", XmlHelper.WritePointF(points["o"]));
                w.WriteElementString("PointA", XmlHelper.WritePointF(points["a"]));
                w.WriteElementString("PointB", XmlHelper.WritePointF(points["b"]));
            }

            if (ShouldSerializeStyle(filter))
            {
                w.WriteStartElement("DrawingStyle");
                style.WriteXml(w);
                w.WriteEndElement();
            }

            if (ShouldSerializeFading(filter))
            {
                w.WriteStartElement("InfosFading");
                infosFading.WriteXml(w);
                w.WriteEndElement();
            }

            if (ShouldSerializeAll(filter))
            {
                // Spreadsheet support.
                w.WriteStartElement("Measure");
                int angle = (int)Math.Floor(angleHelper.CalibratedAngle.Sweep);
                w.WriteAttributeString("UserAngle", angle.ToString());
                w.WriteEndElement();
            }
        }
        #endregion
        
        #region IInitializable implementation
        public void InitializeMove(PointF point, Keys modifiers)
        {
            MoveHandle(point, 3, modifiers);
        }
        public string InitializeCommit(PointF point)
        {
            initializing = false;
            return null;
        }
        public string InitializeEnd(bool cancelCurrentPoint)
        {
            return null;
        }
        #endregion
        
        #region ITrackable implementation and support.
        public TrackingProfile CustomTrackingProfile
        {
            get { return null; }
        }
        public Dictionary<string, PointF> GetTrackablePoints()
        {
            return points;
        }
        public void SetTracking(bool tracking)
        {
            this.tracking = tracking;
        }
        public void SetTrackablePointValue(string name, PointF value)
        {
            if(!points.ContainsKey(name))
                throw new ArgumentException("This point is not bound.");
            
            points[name] = value;
        }
        private void SignalAllTrackablePointsMoved()
        {
            if(TrackablePointMoved == null)
                return;
            
            foreach(KeyValuePair<string, PointF> p in points)
                TrackablePointMoved(this, new TrackablePointMovedEventArgs(p.Key, p.Value));
        }
        private void SignalTrackablePointMoved(string name)
        {
            if(TrackablePointMoved == null || !points.ContainsKey(name))
                return;
            
            TrackablePointMoved(this, new TrackablePointMovedEventArgs(name, points[name]));
        }
        #endregion
        
        #region Specific context menu
        private void mnuInvertAngle_Click(object sender, EventArgs e)
        {
            PointF temp = points["a"];
            points["a"] = points["b"];
            points["b"] = temp;
            SignalAllTrackablePointsMoved();
            CallInvalidateFromMenu(sender);
        }
        #endregion
        
        #region Lower level helpers
        private void BindStyle()
        {
            style.Bind(styleHelper, "Bicolor", "line color");
        }
        private void ComputeValues(IImageToViewportTransformer transformer)
        {
            FixIfNull(transformer);
            angleHelper.Update(points["o"], points["a"], points["b"], 0, Color.Transparent, CalibrationHelper, transformer);
        }
        private void FixIfNull(IImageToViewportTransformer transformer)
        {
            int length = transformer.Untransform(50);

            if (points["a"].NearlyCoincideWith(points["o"]))
                points["a"] = points["o"].Translate(0, -length);

            if (points["b"].NearlyCoincideWith(points["o"]))
                points["b"] = points["o"].Translate(length, 0);
        }
        private bool IsPointInObject(PointF point)
        {
            return angleHelper.Hit(point);
        }
        #endregion
    } 
}