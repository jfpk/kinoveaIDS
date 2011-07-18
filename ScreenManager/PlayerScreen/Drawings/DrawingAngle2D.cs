/*
Copyright © Joan Charmant 2008.
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
using System.Reflection;
using System.Resources;
using System.Threading;
using System.Windows.Forms;
using System.Xml;

using Kinovea.ScreenManager.Languages;
using Kinovea.Services;

namespace Kinovea.ScreenManager
{
    public class DrawingAngle2D : AbstractDrawing, IXMLSerializable, IDecorable, IInitializable
    {
        #region Properties
        public DrawingType DrawingType
        {
        	get { return DrawingType.Angle; }
        }
        public DrawingStyle Style
        {
        	get { return m_Style; }
        }
        public override InfosFading infosFading
        {
            get{ return m_InfosFading;}
            set{ m_InfosFading = value;}
        }
		public override Capabilities Caps
		{
			get { return Capabilities.ConfigureColor | Capabilities.Fading; }
		}
		public override List<ToolStripMenuItem> ContextMenu
		{
			get 
			{
				// Rebuild the menu to get the localized text.
				List<ToolStripMenuItem> contextMenu = new List<ToolStripMenuItem>();
        		
				mnuInvertAngle.Text = ScreenManagerLang.mnuInvertAngle;
        		contextMenu.Add(mnuInvertAngle);
        		
				return contextMenu; 
			}
		}
        #endregion

        #region Members
        // Core
        private Point PointO;
        private Point PointA;
        private Point PointB;
        private double m_fStretchFactor;
        private Point m_DirectZoomTopLeft;

        // Decoration
        private DrawingStyle m_Style;
        private InfosTextDecoration m_InfosStyle;		// Line color is m_InfosStyle.BackColor
        private InfosTextDecoration m_MemoInfosStyle;
        private Color m_BackgroundFillColor;			// Computed from line color.
        private LabelBackground m_LabelBackground = new LabelBackground();
        
        private static readonly int m_iDefaultBackgroundAlpha = 128;
        private static readonly double m_fLabelDistance = 40.0;
        
        // Fading
        private InfosFading m_InfosFading;
        
        // Computed
        private Point RescaledPointO;
        private Point RescaledPointA;
        private Point RescaledPointB;
        private Point m_BoundingPoint;
        private double m_fRadius;
        private float m_fStartAngle;
        private float m_fSweepAngle;	// This is the actual value of the angle.
        
        // Context menu
        private ToolStripMenuItem mnuInvertAngle = new ToolStripMenuItem();
		private DelegateScreenInvalidate m_invalidate;
        #endregion

        #region Constructor
        public DrawingAngle2D(int Ox, int Oy, int Ax, int Ay, int Bx, int By, long _iTimestamp, 
                              long _iAverageTimeStampsPerFrame, DelegateScreenInvalidate _invalidate, DrawingStyle _Style)
        {
            // Core
            PointO = new Point(Ox, Oy);
            PointA = new Point(Ax, Ay);
            PointB = new Point(Bx, By);
            m_fStretchFactor = 1.0;
            m_DirectZoomTopLeft = new Point(0, 0);

            // Decoration
            m_Style = _Style.Clone();
            m_InfosStyle = new InfosTextDecoration("Arial", 12, FontStyle.Bold, Color.White, Color.DarkOliveGreen);
            UpdateBackgroundFillColor();
            
            // Fading
            m_InfosFading = new InfosFading(_iTimestamp, _iAverageTimeStampsPerFrame);

            // Computed
            RescaleCoordinates(m_fStretchFactor, m_DirectZoomTopLeft);
            m_BoundingPoint = new Point(0, 0);
            
            // Context menu
            mnuInvertAngle.Click += new EventHandler(mnuInvertAngle_Click);
			mnuInvertAngle.Image = Properties.Drawings.angleinvert;
			
			m_invalidate = _invalidate;
        }
        #endregion

        #region AbstractDrawing Implementation
        public override void Draw(Graphics _canvas, double _fStretchFactor, bool _bSelected, long _iCurrentTimestamp, Point _DirectZoomTopLeft)
        {
            double fOpacityFactor = m_InfosFading.GetOpacityFactor(_iCurrentTimestamp);

            if (fOpacityFactor > 0)
            {
                // Rescale the points.
                m_fStretchFactor = _fStretchFactor;
                m_DirectZoomTopLeft = new Point(_DirectZoomTopLeft.X, _DirectZoomTopLeft.Y);
                RescaleCoordinates(m_fStretchFactor, m_DirectZoomTopLeft);
           
                //----------------------------------------------------------
                // Draw disk section
                // Unfortunately we need to compute everything at each draw
                // (draw may be triggered on image resize)
                //----------------------------------------------------------            
                ComputeFillRegion();

                SolidBrush brushFill = new SolidBrush(Color.FromArgb((int)((double)m_iDefaultBackgroundAlpha * fOpacityFactor), m_BackgroundFillColor));
                Color edgeColor = m_InfosStyle.GetFadingBackColor(fOpacityFactor);
                Pen penEdges = new Pen(edgeColor);
                SolidBrush brushEdges = new SolidBrush(edgeColor);
                
                _canvas.FillPie(brushFill, (float)m_BoundingPoint.X, (float)m_BoundingPoint.Y, (float)m_fRadius * 2, (float)m_fRadius * 2, m_fStartAngle, m_fSweepAngle);
                _canvas.DrawPie(penEdges, (float)m_BoundingPoint.X, (float)m_BoundingPoint.Y, (float)m_fRadius * 2, (float)m_fRadius * 2, m_fStartAngle, m_fSweepAngle);

                //-----------------------------
                // Draw the edges 
                //-----------------------------
                _canvas.DrawLine(penEdges, RescaledPointO.X, RescaledPointO.Y, RescaledPointA.X, RescaledPointA.Y);
                _canvas.DrawLine(penEdges, RescaledPointO.X, RescaledPointO.Y, RescaledPointB.X, RescaledPointB.Y);

                //-----------------------------
                // Draw handlers
                //-----------------------------
                _canvas.DrawEllipse(penEdges, GetRescaledHandleRectangle(1));
                _canvas.FillEllipse(brushEdges, GetRescaledHandleRectangle(2));
                _canvas.FillEllipse(brushEdges, GetRescaledHandleRectangle(3));
				
                brushFill.Dispose();
                penEdges.Dispose();
                brushEdges.Dispose();
                
                //----------------------------
                // Draw Measure
                //----------------------------

                // We try to be inside the pie, so we compute the bissectrice and do some trigo.
                // We start the text on the bissectrice, at a distance of iTextRadius.
                SolidBrush fontBrush = new SolidBrush(m_InfosStyle.GetFadingForeColor(fOpacityFactor));
                
                int angle = (int)Math.Floor(-m_fSweepAngle);
				string label = angle.ToString() + "°";
                Font tempFont = m_InfosStyle.GetInternalFont((float)m_fStretchFactor);
				SizeF labelSize = _canvas.MeasureString(label, tempFont);
                Point TextOrigin = GetTextPosition(labelSize);
                
                // Background
                m_LabelBackground.Location = TextOrigin;
            	int radius = (int)(tempFont.Size / 2);
            	m_LabelBackground.Draw(_canvas, fOpacityFactor, radius, (int)labelSize.Width, (int)labelSize.Height, m_InfosStyle.BackColor);
        
                // Text
				_canvas.DrawString(label, tempFont, fontBrush, m_LabelBackground.TextLocation);
                
                tempFont.Dispose();
                fontBrush.Dispose();
            }
        }
        public override int HitTest(Point _point, long _iCurrentTimestamp)
        {
            //-----------------------------------------------------
            // This function is used by the PointerTool 
            // to know if we hit this particular drawing and where.
            // _point is mouse coordinates already descaled.
            // Hit Result: -1: miss, 0: on object, 1+: on handle.
            //-----------------------------------------------------
            int iHitResult = -1;
            double fOpacityFactor = m_InfosFading.GetOpacityFactor(_iCurrentTimestamp);
            if (fOpacityFactor > 0)
            {
                if (GetHandleRectangle(1).Contains(_point))
                {
                    iHitResult = 1;
                }
                else if (GetHandleRectangle(2).Contains(_point))
                {
                    iHitResult = 2;
                }
                else if (GetHandleRectangle(3).Contains(_point))
                {
                    iHitResult = 3;
                }
                else
                {
                    if (IsPointInObject(_point))
                    {
                        iHitResult = 0;
                    }
                }
            }
            return iHitResult;
        }
        public override void MoveHandle(Point point, int handleNumber)
        {
            // Move the specified handle to the specified coordinates.
            // In Angle2D, handles are directly mapped to the endpoints of the lines.
            // _point is mouse coordinates already descaled.
            switch (handleNumber)
            {
                case 1:
                    PointO = point;
                    break;
                case 2:
                    PointA = point;
                    break;
                case 3:
                    PointB = point;
                    break;
                default:
                    break;
            }
            
            // Update scaled coordinates accordingly.
            RescaleCoordinates(m_fStretchFactor, m_DirectZoomTopLeft);
        }
        public override void MoveDrawing(int _deltaX, int _deltaY, Keys _ModifierKeys)
        {
            // _delatX and _delatY are mouse delta already descaled.
            PointO.X += _deltaX;
            PointO.Y += _deltaY;

            PointA.X += _deltaX;
            PointA.Y += _deltaY;

            PointB.X += _deltaX;
            PointB.Y += _deltaY;

            // Update scaled coordinates accordingly.
            RescaleCoordinates(m_fStretchFactor, m_DirectZoomTopLeft);
        }
		#endregion
		
        public override string ToString()
        {
            // Return the name of the tool used to draw this drawing.
            ResourceManager rm = new ResourceManager("Kinovea.ScreenManager.Languages.ScreenManagerLang", Assembly.GetExecutingAssembly());
            return rm.GetString("ToolTip_DrawingToolAngle2D", Thread.CurrentThread.CurrentUICulture);
        }
        public override int GetHashCode()
        {
            // Combine all relevant fields with XOR to get the Hash.
            int iHash = PointO.GetHashCode();
            iHash ^= PointA.GetHashCode();
            iHash ^= PointB.GetHashCode();
            iHash ^= m_InfosStyle.GetHashCode();
            return iHash;
        }        
        public static AbstractDrawing FromXml(XmlTextReader _xmlReader, PointF _scale)
        {
            DrawingAngle2D da = new DrawingAngle2D(0,0,0,0,0,0,0,0, null, null);

            while (_xmlReader.Read())
            {
                if (_xmlReader.IsStartElement())
                {
                    if (_xmlReader.Name == "PointO")
                    {
                        da.PointO= XmlHelper.PointParse(_xmlReader.ReadString(), ';');   
                    }
                    else if (_xmlReader.Name == "PointA")
                    {
                        da.PointA = XmlHelper.PointParse(_xmlReader.ReadString(), ';');
                    }
                    else if (_xmlReader.Name == "PointB")
                    {
                        da.PointB = XmlHelper.PointParse(_xmlReader.ReadString(), ';');
                    }
                    else if (_xmlReader.Name == "TextDecoration")
                    {
                    	da.m_InfosStyle = InfosTextDecoration.FromXml(_xmlReader);
                    	da.UpdateBackgroundFillColor();
                    }
                    else if (_xmlReader.Name == "InfosFading")
                    {
                        da.m_InfosFading.FromXml(_xmlReader);
                    }
                    else
                    {
                        // forward compatibility : ignore new fields. 
                    }
                }
                else if (_xmlReader.Name == "Drawing")
                {
                    break;
                }
                else
                {
                    // Fermeture d'un tag interne.
                }
            }

            // We only scale the position (PointO), not the size of the edges, 
            // because changing the size of the edges will change angle value.
            Point ShiftOA = new Point(da.PointA.X - da.PointO.X, da.PointA.Y - da.PointO.Y);
            Point ShiftOB = new Point(da.PointB.X - da.PointO.X, da.PointB.Y - da.PointO.Y);

            da.PointO = new Point((int)((float)da.PointO.X * _scale.X), (int)((float)da.PointO.Y * _scale.Y));
            da.PointA = new Point(da.PointO.X + ShiftOA.X, da.PointO.Y + ShiftOA.Y);
            da.PointB = new Point(da.PointO.X + ShiftOB.X, da.PointO.Y + ShiftOB.Y);

            da.RescaleCoordinates(da.m_fStretchFactor, da.m_DirectZoomTopLeft);
            return da;
        }
    
        #region IXMLSerializable implementation
        public void ToXmlString(XmlTextWriter _xmlWriter)
        {
            _xmlWriter.WriteStartElement("Drawing");
            _xmlWriter.WriteAttributeString("Type", "DrawingAngle2D");

            // PointO
            _xmlWriter.WriteStartElement("PointO");
            _xmlWriter.WriteString(PointO.X.ToString() + ";" + PointO.Y.ToString());
            _xmlWriter.WriteEndElement();

            // PointA
            _xmlWriter.WriteStartElement("PointA");
            _xmlWriter.WriteString(PointA.X.ToString() + ";" + PointA.Y.ToString());
            _xmlWriter.WriteEndElement();

            // PointB
            _xmlWriter.WriteStartElement("PointB");
            _xmlWriter.WriteString(PointB.X.ToString() + ";" + PointB.Y.ToString());
            _xmlWriter.WriteEndElement();

            m_InfosStyle.ToXml(_xmlWriter);
            m_InfosFading.ToXml(_xmlWriter, false);

            // This is only for spreadsheet export support. These values are not read at import.
        	_xmlWriter.WriteStartElement("Measure");        	
        	int angle = (int)Math.Floor(-m_fSweepAngle);        	
        	_xmlWriter.WriteAttributeString("UserAngle", angle.ToString());
        	_xmlWriter.WriteEndElement();
            
            // </Drawing>
            _xmlWriter.WriteEndElement();
        }
        #endregion
        
        #region IDecorable implementation
        public void UpdateDecoration(Color _color)
        {
        	m_InfosStyle.Update(_color);
        	// Compute the background fill color from the edges color.
        	// Fixing the text color has been done within m_InfosStyle,
            // taking the line color as reference. (Not optimal)       	
            UpdateBackgroundFillColor();
        }
        public void UpdateDecoration(LineStyle _style)
        {
        	throw new Exception(String.Format("{0}, The method or operation is not implemented.", this.ToString()));
        }
        public void UpdateDecoration(int _iFontSize)
        {
        	// Actually not used for now.
        	m_InfosStyle.Update(_iFontSize);
        }
        public void MemorizeDecoration()
        {
        	m_MemoInfosStyle = m_InfosStyle.Clone();
        }
        public void RecallDecoration()
        {
        	m_InfosStyle = m_MemoInfosStyle.Clone();
        }
        #endregion
        
        #region IInitializable implementation
        public void ContinueSetup(Point point)
		{
			MoveHandle(point, 2);
		}
        #endregion
        
        #region Specific context menu
        private void mnuInvertAngle_Click(object sender, EventArgs e)
		{
        	Point temp = PointA;
        	PointA = PointB;
        	PointB = temp;
        	RescaleCoordinates(m_fStretchFactor, m_DirectZoomTopLeft);
        	
        	if(m_invalidate != null)
        	{
        		m_invalidate();
        	}
		}
        public void InvertAngle()
        {
        	Point temp = PointA;
        	PointA = PointB;
        	PointB = temp;
        	RescaleCoordinates(m_fStretchFactor, m_DirectZoomTopLeft);
        }
        #endregion
        
        #region Lower level helpers
        private void UpdateBackgroundFillColor()
        {
        	// compute the background fill color from the edges color.
            int r = m_InfosStyle.BackColor.R + 69;
            int g = m_InfosStyle.BackColor.G + 98;
            int b = m_InfosStyle.BackColor.B + 3;

            if (r > 255) r = 255;
            if (g > 255) g = 255;
            if (b > 255) b = 255;

            m_BackgroundFillColor = Color.FromArgb(r, g, b);
        }
        private void ComputeFillRegion()
        {

            // 2.1. Compute Radius (Smallest segment get to be the radius)
            double OALength = Math.Sqrt(((RescaledPointA.X - RescaledPointO.X) * (RescaledPointA.X - RescaledPointO.X)) + ((RescaledPointA.Y - RescaledPointO.Y) * (RescaledPointA.Y - RescaledPointO.Y)));
            double OBLength = Math.Sqrt(((RescaledPointB.X - RescaledPointO.X) * (RescaledPointB.X - RescaledPointO.X)) + ((RescaledPointB.Y - RescaledPointO.Y) * (RescaledPointB.Y - RescaledPointO.Y)));

            if(OALength == 0 || OBLength == 0)
            {
            	if(OALength == 0)
	            {
	            	PointA.X = PointO.X + 50;
	                PointA.Y = PointO.Y;
	            }
	            
            	if(OBLength == 0)
	            {
	                PointB.X = PointO.X;
	                PointB.Y = PointO.Y - 50;
            	}
	            	
                RescaleCoordinates(m_fStretchFactor, m_DirectZoomTopLeft);
                OALength = Math.Sqrt(((RescaledPointA.X - RescaledPointO.X) * (RescaledPointA.X - RescaledPointO.X)) + ((RescaledPointA.Y - RescaledPointO.Y) * (RescaledPointA.Y - RescaledPointO.Y)));
                OBLength = Math.Sqrt(((RescaledPointB.X - RescaledPointO.X) * (RescaledPointB.X - RescaledPointO.X)) + ((RescaledPointB.Y - RescaledPointO.Y) * (RescaledPointB.Y - RescaledPointO.Y)));        	       
            }

            m_fRadius = Math.Min(OALength, OBLength);
            if(m_fRadius > 20) m_fRadius -= 10;

            // 2.2. Bounding box top/left
            m_BoundingPoint.X = RescaledPointO.X - (int)m_fRadius;
            m_BoundingPoint.Y = RescaledPointO.Y - (int)m_fRadius;

            // 2.3. Start and stop angles
            double fOARadians = Math.Atan((double)(RescaledPointA.Y - RescaledPointO.Y) / (double)(RescaledPointA.X - RescaledPointO.X));
            double fOBRadians = Math.Atan((double)(RescaledPointB.Y - RescaledPointO.Y) / (double)(RescaledPointB.X - RescaledPointO.X));

            double iOADegrees;
            if (PointA.X < PointO.X)
            {
                // angle obtu (entre 0° et OA)
                iOADegrees = (fOARadians * (180 / Math.PI)) - 180;
            }
            else
            {
                // angle aigu
                iOADegrees = fOARadians * (180 / Math.PI);
            }

            double iOBDegrees;
            if (PointB.X < PointO.X)
            {
                // Angle obtu
                iOBDegrees = (fOBRadians * (180 / Math.PI)) - 180;
            }
            else
            {
                // angle aigu
                iOBDegrees = fOBRadians * (180 / Math.PI);
            }

            // Always go direct orientation. The sweep always go from OA to OB, and is always negative.
            m_fStartAngle = (float)iOADegrees;
            if (iOADegrees > iOBDegrees)
            {
                m_fSweepAngle = -((float)iOADegrees - (float)iOBDegrees);
            }
            else
            {
                m_fSweepAngle = -((float)360.0 - ((float)iOBDegrees - (float)iOADegrees));
            }
        }
        private void RescaleCoordinates(double _fStretchFactor, Point _DirectZoomTopLeft)
        {
            RescaledPointO = new Point((int)((double)(PointO.X - _DirectZoomTopLeft.X) * _fStretchFactor), (int)((double)(PointO.Y - _DirectZoomTopLeft.Y) * _fStretchFactor));
            RescaledPointA = new Point((int)((double)(PointA.X - _DirectZoomTopLeft.X) * _fStretchFactor), (int)((double)(PointA.Y - _DirectZoomTopLeft.Y) * _fStretchFactor));
            RescaledPointB = new Point((int)((double)(PointB.X - _DirectZoomTopLeft.X) * _fStretchFactor), (int)((double)(PointB.Y - _DirectZoomTopLeft.Y) * _fStretchFactor));
        }
        private Rectangle GetHandleRectangle(int _handle)
        {
            //----------------------------------------------------------------------------
            // This function is only used for Hit Testing.
            // The Rectangle here is bigger than the bounding box of the handlers circles.
            //----------------------------------------------------------------------------
            Rectangle handle;
            int widen = 10;

            switch (_handle)
            {
                case 1:
                    handle = new Rectangle(PointO.X - widen, PointO.Y - widen, widen * 2, widen*2);
                    break;
                case 2:
                    handle = new Rectangle(PointA.X - widen, PointA.Y - widen, widen * 2, widen * 2);
                    break;
                case 3:
                    handle = new Rectangle(PointB.X - widen, PointB.Y - widen, widen * 2, widen * 2);
                    break;
                default:
                    handle = new Rectangle(PointO.X - widen, PointO.Y - widen, widen * 2, widen * 2);
                    break;
            }

            return handle;
        }
        private Rectangle GetRescaledHandleRectangle(int _handle)
        {
        	// This function is only used for drawing.
            Rectangle handle;

            switch (_handle)
            {
                case 1:
                    handle = new Rectangle(RescaledPointO.X - 3, RescaledPointO.Y - 3, 6, 6);
                    break;
                case 2:
                    handle = new Rectangle(RescaledPointA.X - 3, RescaledPointA.Y - 3, 6, 6);
                    break;
                case 3:
                    handle = new Rectangle(RescaledPointB.X - 3, RescaledPointB.Y - 3, 6, 6);
                    break;
                default:
                    handle = new Rectangle(RescaledPointO.X - 3, RescaledPointO.Y - 3, 6, 6);
                    break;
            }

            return handle;
        }
        private bool IsPointInObject(Point _point)
        {
            // _point is already descaled.

            bool bIsPointInObject = false;
            if (m_fRadius > 0)
            {
                GraphicsPath areaPath = new GraphicsPath();
                
                double OALength = Math.Sqrt(((PointA.X - PointO.X) * (PointA.X - PointO.X)) + ((PointA.Y - PointO.Y) * (PointA.Y - PointO.Y)));
                double OBLength = Math.Sqrt(((PointB.X - PointO.X) * (PointB.X - PointO.X)) + ((PointB.Y - PointO.Y) * (PointB.Y - PointO.Y)));
                double iRadius = Math.Min(OALength, OBLength);
                Point unscaledBoundingPoint = new Point(PointO.X - (int)iRadius, PointO.Y - (int)iRadius);

                areaPath.AddPie(unscaledBoundingPoint.X, unscaledBoundingPoint.Y, (float)iRadius * 2, (float)iRadius * 2, m_fStartAngle, m_fSweepAngle);

                // Create region from the path
                Region areaRegion = new Region(areaPath);
                bIsPointInObject = new Region(areaPath).IsVisible(_point);
            }

            return bIsPointInObject;
        }
        private Point GetTextPosition(SizeF _labelSize)
        {
            // return a point at which the text should start.

            // Get bissect angle in degrees
            float iBissect = m_fStartAngle + (m_fSweepAngle / 2);
            if (iBissect < 0)
            {
                iBissect += 360;
            }

            double fRadiansBissect = (Math.PI / 180) * iBissect;
            double fSin = Math.Sin((double)fRadiansBissect);
            double fCos = Math.Cos((double)fRadiansBissect);
            
            double fOpposed = fSin * m_fLabelDistance;
            double fAdjacent = fCos * m_fLabelDistance;

            Point TextOrigin = new Point(RescaledPointO.X, RescaledPointO.Y);
            TextOrigin.X = TextOrigin.X + (int)fAdjacent - (int)(_labelSize.Width/2);
            TextOrigin.Y = TextOrigin.Y + (int)fOpposed - (int)(_labelSize.Height/2);
			
			return TextOrigin;
        }
        #endregion
    }

       
}