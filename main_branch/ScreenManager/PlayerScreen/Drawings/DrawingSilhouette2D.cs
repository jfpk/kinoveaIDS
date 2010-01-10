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

using Kinovea.Services;
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Reflection;
using System.Resources;
using System.Threading;
using System.Xml;

namespace Kinovea.ScreenManager
{
    public class DrawingSilhouette2D : AbstractDrawing
    {
        #region Properties
        public override DrawingToolType ToolType
        {
        	get { return DrawingToolType.Angle2D; }
        }
        public override InfosFading infosFading
        {
            get{ return m_InfosFading;}
            set{ m_InfosFading = value;}
        }
        #endregion

        #region Members
        // Core
        private Point PointO;
        private Point PointR;
        private Point PointL;                       // Point for the "leg"
        private double m_fStretchFactor;
        private Point m_DirectZoomTopLeft;

        // Decoration
        private InfosTextDecoration m_InfosStyle;		// Line color is m_InfosStyle.BackColor
        private InfosTextDecoration m_MemoInfosStyle;
        private Color m_BackgroundFillColor;			// Computed from line color.
        
        private static readonly int m_iDefaultBackgroundAlpha = 128;
        
        //private Color PenEdgesColor;	
        //private string FontName;
        //private int FontSize;
        //private Color FontBrushColor;
        //private Color MemoColor;

        // Fading
        private InfosFading m_InfosFading;
        
        // Computed
        private Point RescaledPointO;
        private Point RescaledPointR;
        private Point RescaledPointL;
        private Point m_BoundingPoint;
        private double m_fRadius;
        #endregion

        #region Constructor
        public DrawingSilhouette2D(int Ox, int Oy, int Rx, int Ry, long _iTimestamp, long _iAverageTimeStampsPerFrame)
        {
            // Core
            PointO = new Point(Ox, Oy);
            PointR = new Point(Rx, Oy);
            PointL = new Point(Rx, Oy+150);

            m_fStretchFactor = 1.0;
            m_DirectZoomTopLeft = new Point(0, 0);

            // Decoration
            m_InfosStyle = new InfosTextDecoration("Arial", 12, FontStyle.Bold, Color.White, Color.DarkOliveGreen);
            UpdateBackgroundFillColor();
            
            // Fading
            m_InfosFading = new InfosFading(_iTimestamp, _iAverageTimeStampsPerFrame);

            // Computed
            RescaleCoordinates(m_fStretchFactor, m_DirectZoomTopLeft);
            m_BoundingPoint = new Point(0, 0);
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

                SolidBrush FillBrush = new SolidBrush(Color.FromArgb((int)((double)m_iDefaultBackgroundAlpha * fOpacityFactor), m_BackgroundFillColor));
                Pen PenEdges = new Pen(m_InfosStyle.GetFadingBackColor(fOpacityFactor));

                m_fRadius = Math.Sqrt(Math.Pow(RescaledPointO.X - RescaledPointR.X, 2) + Math.Pow(RescaledPointO.Y - RescaledPointR.Y, 2));

                _canvas.FillEllipse(FillBrush, (float)RescaledPointO.X - (float)m_fRadius, (float)RescaledPointO.Y - (float)m_fRadius, (float)m_fRadius * 2, (float)m_fRadius * 2);
                _canvas.DrawEllipse(PenEdges, (float)RescaledPointO.X - (float)m_fRadius, (float)RescaledPointO.Y - (float)m_fRadius, (float)m_fRadius * 2, (float)m_fRadius * 2);


                float distance_diff = ((float)RescaledPointL.X - (float)RescaledPointO.X);
                _canvas.DrawLine(PenEdges, (float)RescaledPointL.X, (float)RescaledPointL.Y, (float)RescaledPointL.X, (float)RescaledPointO.Y);
                _canvas.DrawLine(PenEdges, (float)RescaledPointL.X-2*distance_diff, (float)RescaledPointL.Y, (float)RescaledPointL.X-2*distance_diff, (float)RescaledPointO.Y);

                //-----------------------------
                // Draw handlers
                //-----------------------------
                if (_bSelected)
                    PenEdges.Width = 2;

                _canvas.DrawEllipse(PenEdges, GetRescaledHandleRectcircle(1));
                _canvas.DrawEllipse(PenEdges, GetRescaledHandleRectcircle(2));

            }
        }
        public override void MoveHandleTo(Point point, int handleNumber)
        {
            // Move the specified handle to the specified coordinates.
            // In Circle2D, handles are directly mapped to the endpoints of the lines.
            // _point is mouse coordinates already descaled.

            switch (handleNumber)
            {
                case 1:
                    PointR.X = point.X;
                    break;
                case 2:
                    PointL = point;
                    break;
                default:
                    break;
            }
            
            // Update scaled coordinates accordingly.
            RescaleCoordinates(m_fStretchFactor, m_DirectZoomTopLeft);
        }
        public override void MoveDrawing(int _deltaX, int _deltaY)
        {
            // _delatX and _delatY are mouse delta already descaled.
            PointO.X += _deltaX;
            PointO.Y += _deltaY;

            PointR.X += _deltaX;
            PointR.Y += _deltaY;

            PointL.X += _deltaX;
            PointL.Y += _deltaY;

            // Update scaled coordinates accordingly.
            RescaleCoordinates(m_fStretchFactor, m_DirectZoomTopLeft);
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
                if (GetHandleRectcircle(1).Contains(_point))
                {
                    iHitResult = 1;
                }
                else if (GetHandleRectcircle(2).Contains(_point))
                {
                    iHitResult = 2;
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
        public override void ToXmlString(XmlTextWriter _xmlWriter)
        {
            _xmlWriter.WriteStartElement("Drawing");
            _xmlWriter.WriteAttributeString("Type", "DrawingCircle2D");

            // PointO
            _xmlWriter.WriteStartElement("PointO");
            _xmlWriter.WriteString(PointO.X.ToString() + ";" + PointO.Y.ToString());
            _xmlWriter.WriteEndElement();

            // PointR
            _xmlWriter.WriteStartElement("PointR");
            _xmlWriter.WriteString(PointR.X.ToString() + ";" + PointR.Y.ToString());
            _xmlWriter.WriteEndElement();

            // PointR
            _xmlWriter.WriteStartElement("PointL");
            _xmlWriter.WriteString(PointL.X.ToString() + ";" + PointL.Y.ToString());
            _xmlWriter.WriteEndElement();

            m_InfosStyle.ToXml(_xmlWriter);
            m_InfosFading.ToXml(_xmlWriter, false);

            // </Drawing>
            _xmlWriter.WriteEndElement();
        }
        
        public override string ToString()
        {
            // Return the name of the tool used to draw this drawing.
            ResourceManager rm = new ResourceManager("Kinovea.ScreenManager.Languages.ScreenManagerLang", Assembly.GetExecutingAssembly());
            return rm.GetString("ToolTip_DrawingTooCircle2D", Thread.CurrentThread.CurrentUICulture);
        }
        public override int GetHashCode()
        {
            // Combine all relevant fields with XOR to get the Hash.
            int iHash = PointO.GetHashCode();
            iHash ^= PointR.GetHashCode();
            iHash ^= PointL.GetHashCode();
            iHash ^= m_InfosStyle.GetHashCode();
            return iHash;
        }
        
        public override void UpdateDecoration(Color _color)
        {
        	m_InfosStyle.Update(_color);
        	// Compute the background fill color from the edges color.
        	// Fixing the text color has been done within m_InfosStyle,
            // taking the line color as reference. (Not optimal)       	
            UpdateBackgroundFillColor();
        }
        public override void UpdateDecoration(LineStyle _style)
        {
        	throw new Exception(String.Format("{0}, The method or operation is not implemented.", this.ToString()));
        }
        public override void UpdateDecoration(int _iFontSize)
        {
        	// Actually not used for now.
        	m_InfosStyle.Update(_iFontSize);
        }
        public override void MemorizeDecoration()
        {
        	m_MemoInfosStyle = m_InfosStyle.Clone();
        }
        public override void RecallDecoration()
        {
        	m_InfosStyle = m_MemoInfosStyle.Clone();
        }
        
        public static AbstractDrawing FromXml(XmlTextReader _xmlReader, PointF _scale)
        {
            DrawingSilhouette2D dc = new DrawingSilhouette2D(0, 0, 0, 0, 0, 0);

            while (_xmlReader.Read())
            {
                if (_xmlReader.IsStartElement())
                {
                    if (_xmlReader.Name == "PointO")
                    {
                        dc.PointO= XmlHelper.PointParse(_xmlReader.ReadString(), ';');   
                    }
                    else if (_xmlReader.Name == "PointR")
                    {
                        dc.PointR = XmlHelper.PointParse(_xmlReader.ReadString(), ';');
                    }
                    else if (_xmlReader.Name == "PointL")
                    {
                        dc.PointL = XmlHelper.PointParse(_xmlReader.ReadString(), ';');
                    }
                    else if (_xmlReader.Name == "TextDecoration")
                    {
                    	dc.m_InfosStyle = InfosTextDecoration.FromXml(_xmlReader);
                    	dc.UpdateBackgroundFillColor();
                    }
                    else if (_xmlReader.Name == "InfosFading")
                    {
                        dc.m_InfosFading.FromXml(_xmlReader);
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
            Point ShiftOR = new Point(dc.PointR.X - dc.PointO.X, dc.PointR.Y - dc.PointO.Y);

            dc.PointO = new Point((int)((float)dc.PointO.X * _scale.X), (int)((float)dc.PointO.Y * _scale.Y));
            dc.PointR = new Point(dc.PointO.X + ShiftOR.X, dc.PointO.Y + ShiftOR.Y);
            dc.PointL = new Point(dc.PointL.X + ShiftOR.X, dc.PointL.Y + ShiftOR.Y);

            dc.RescaleCoordinates(dc.m_fStretchFactor, dc.m_DirectZoomTopLeft);
            return dc;
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
        private void RescaleCoordinates(double _fStretchFactor, Point _DirectZoomTopLeft)
        {
            RescaledPointO = new Point((int)((double)(PointO.X - _DirectZoomTopLeft.X) * _fStretchFactor), (int)((double)(PointO.Y - _DirectZoomTopLeft.Y) * _fStretchFactor));
            RescaledPointR = new Point((int)((double)(PointR.X - _DirectZoomTopLeft.X) * _fStretchFactor), (int)((double)(PointR.Y - _DirectZoomTopLeft.Y) * _fStretchFactor));
            RescaledPointL = new Point((int)((double)(PointL.X - _DirectZoomTopLeft.X) * _fStretchFactor), (int)((double)(PointL.Y - _DirectZoomTopLeft.Y) * _fStretchFactor));

        }
        private Rectangle GetHandleRectcircle(int _handle)
        {
            //----------------------------------------------------------------------------
            // This function is only used for Hit Testing.
            // The Rectangle here is bigger than the bounding box of the handlers circles.
            //----------------------------------------------------------------------------
            Rectangle handle;
            int widen = 6;

            switch (_handle)
            {
                case 1:
                    handle = new Rectangle(PointR.X - widen, PointR.Y - widen, widen * 2, widen * 2);
                    break;
                case 2:
                    handle = new Rectangle(PointL.X - widen, PointL.Y - widen, widen * 2, widen * 2);
                    break;
                default:
                    handle = new Rectangle(PointO.X - widen, PointO.Y - widen, widen * 2, widen * 2);
                    break;
            }

            return handle;
        }
        private Rectangle GetRescaledHandleRectcircle(int _handle)
        {
            Rectangle handle;

            switch (_handle)
            {
                case 1:
                    handle = new Rectangle(RescaledPointR.X - 3, RescaledPointR.Y - 3, 6, 6);
                    break;
                case 2:
                    handle = new Rectangle(RescaledPointL.X - 3, RescaledPointL.Y - 3, 6, 6);
                    break;
                default:
                    handle = new Rectangle(RescaledPointR.X - 3, RescaledPointR.Y - 3, 6, 6);
                    break;
            }

            return handle;
        }
        private Rectangle GetShiftedRescaledHandleRectangle(int _handle, int _iLeftShift, int _iTopShift)
        {
            // UNUSED ?

            // Only used on pdf export.
            Rectangle handle = GetRescaledHandleRectcircle(_handle);

            // Hack : we reduce the zone by 1 px each direction because the PDFSharp library will draw too big circles.
            return new Rectangle(handle.Left + _iLeftShift + 1, handle.Top + _iTopShift + 1, handle.Width - 1, handle.Height - 1);
        }
        private bool IsPointInObject(Point _point)
        {
            // _point is already descaled.

            bool bIsPointInObject = false;
            if (m_fRadius > 0)
            {
                GraphicsPath areaPath = new GraphicsPath();


                double iRadius = Math.Sqrt(Math.Pow(PointO.X - PointR.X, 2) + Math.Pow(PointO.Y - PointR.Y, 2));
                Point unscaledBoundingPoint = new Point(PointO.X - (int)iRadius, PointO.Y - (int)iRadius);

                areaPath.AddEllipse(unscaledBoundingPoint.X, unscaledBoundingPoint.Y, (float)iRadius * 2, (float)iRadius * 2);

                // Create region from the path
                Region areaRegion = new Region(areaPath);
                bIsPointInObject = new Region(areaPath).IsVisible(_point);
            }

            return bIsPointInObject;
        }
        #endregion
    }

       
}