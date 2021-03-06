﻿using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Text;
using System.Windows.Forms;

namespace Kinovea.ScreenManager
{
    /// <summary>
    /// A helper class to draw a rounded rectangle for labels.
    /// The rectangle can have a drop shape (top left and bottom right corners are "pointy").
    /// It can also have a hidden handler in the bottom right corner.
    /// Change of size resulting from moving the hidden handler is the responsibility of the caller.
    /// </summary>
    public class RoundedRectangle
    {
        #region Properties
        public RectangleF Rectangle
        {
            get { return rectangle; }
            set { rectangle = value; }
        }
        public PointF Center
        {
            get { return new PointF(rectangle.X + rectangle.Width / 2, rectangle.Y + rectangle.Height / 2); }
        }
        public float X
        {
            get { return rectangle.X; }
        }
        public float Y
        {
            get { return rectangle.Y; }
        }
        #endregion

        #region Members
        private RectangleF rectangle;
        #endregion

        /// <summary>
        /// Draw a rounded rectangle on the provided canvas. 
        /// This method is typically used after applying a transform to the original rectangle.
        /// </summary>
        /// <param name="canvas">The graphics object on which to draw</param>
        /// <param name="rect">The rectangle specifications</param>
        /// <param name="brush">Brush to draw with</param>
        /// <param name="radius">Radius of the rounded corners</param>
        public static void Draw(Graphics canvas, RectangleF rect, SolidBrush brush, int radius, bool dropShape, bool contour, Pen penContour)
        {
            float diameter = 2F * radius;
            RectangleF arc = new RectangleF(rect.Location, new SizeF(diameter, diameter));
        
            GraphicsPath gp = new GraphicsPath();
            gp.StartFigure();
            
            if(dropShape)
                gp.AddLine(arc.Left, arc.Top, arc.Right, arc.Top);
            else
                gp.AddArc(arc, 180, 90);
            
            arc.X = rect.Right - diameter;
            gp.AddArc(arc, 270, 90);

            arc.Y = rect.Bottom - diameter;
             if(dropShape)
                gp.AddLine(arc.Right, arc.Top, arc.Right, arc.Bottom);
            else
                gp.AddArc(arc, 0, 90);
            
            arc.X = rect.Left;
            gp.AddArc(arc, 90, 90);
            
            gp.CloseFigure();
            
            canvas.FillPath(brush, gp);
            
            if(contour)
                canvas.DrawPath(penContour, gp);
            
            gp.Dispose();
        }
        public int HitTest(PointF point, bool hiddenHandle, IImageToViewportTransformer transformer)
        {
            int result = -1;

            SizeF size = rectangle.Size;
            RectangleF hitArea = rectangle;

            if (hiddenHandle)
            {
                int boxSide = (int)(size.Width / 4);
                PointF bottomRight = new PointF(hitArea.Right, hitArea.Bottom);
                if (bottomRight.Box(boxSide).Contains(point))
                    result = 1;
            }

            if (result < 0 && hitArea.Contains(point))
                result = 0;

            return result;
        }
        public void Move(float dx, float dy)
        {
            rectangle = rectangle.Translate(dx, dy);
        }
        public void CenterOn(PointF point)
        {
            PointF location = new PointF(point.X - rectangle.Size.Width / 2, point.Y - rectangle.Size.Height / 2);
            rectangle = new RectangleF(location, rectangle.Size);
        }
    }
}
