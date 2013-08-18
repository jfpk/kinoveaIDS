﻿using System;
using System.Collections.Generic;
using System.Text;
using System.Drawing;

namespace Kinovea.ScreenManager
{
    /// <summary>
    /// A helper class for drawings using a bounding box.
    /// Basically a wrapper around a rectangle, with added method for moving, hit testing, and drawing.
    /// By convention, the handles id of the bounding box will always be 1 to 4.
    /// When the drawing has other handles, they should start at id 5.
    /// </summary>
    public class BoundingBox
    {
        #region Properties
        public Rectangle Rectangle
        {
            get { return m_Rectangle; }
            set { m_Rectangle = value; }
        }
        #endregion

        #region Members
        private Rectangle m_Rectangle;
        private static Size m_MinimalSize = new Size(50,50);
        #endregion

        public void Draw(Graphics _canvas, Rectangle _rect, Pen _pen, SolidBrush _brush, int _widen)
        {
            _canvas.DrawRectangle(_pen, _rect);
            _canvas.FillEllipse(_brush, _rect.Left - _widen, _rect.Top - _widen, _widen * 2, _widen * 2);
            _canvas.FillEllipse(_brush, _rect.Left - _widen, _rect.Bottom - _widen, _widen * 2, _widen * 2);
            _canvas.FillEllipse(_brush, _rect.Right - _widen, _rect.Top - _widen, _widen * 2, _widen * 2);
            _canvas.FillEllipse(_brush, _rect.Right - _widen, _rect.Bottom - _widen, _widen * 2, _widen * 2);
        }
        public int HitTest(Point point, IImageToViewportTransformer transformer)
        {
            int result = -1;
            
            Point topLeft = m_Rectangle.Location;
            Point topRight = new Point(m_Rectangle.Right, m_Rectangle.Top);
            Point botRight = new Point(m_Rectangle.Right, m_Rectangle.Bottom);
            Point botLeft = new Point(m_Rectangle.Left, m_Rectangle.Bottom);

            int boxSide = transformer.Untransform(6);

            if (topLeft.Box(boxSide).Contains(point))
                result = 1;
            else if (topRight.Box(boxSide).Contains(point))
                result = 2;
            else if (botRight.Box(boxSide).Contains(point))
                result = 3;
            else if (botLeft.Box(boxSide).Contains(point))
                result = 4;
            else if (m_Rectangle.Contains(point))
                result = 0;

            return result;
        }
        public void MoveHandle(Point point, int handleNumber, Size _originalSize, bool keepAspectRatio)
        {
            if(keepAspectRatio)
                MoveHandleKeepAspectRatio(point, handleNumber, _originalSize);
            else
                MoveHandleFree(point, handleNumber);
        }
        public void Move(int _deltaX, int _deltaY)
        {
            m_Rectangle = new Rectangle(m_Rectangle.X + _deltaX, m_Rectangle.Y + _deltaY, m_Rectangle.Width, m_Rectangle.Height);
        }
        public void MoveAndSnap(int _deltaX, int _deltaY, Size _containerSize, int _snapMargin)
        {
            int deltaX = _deltaX;
            int deltaY = _deltaY;
            
            if(m_Rectangle.Left + deltaX < _snapMargin)
                deltaX = - m_Rectangle.Left;
            
            if(m_Rectangle.Right + deltaX > _containerSize.Width - _snapMargin)
                deltaX = _containerSize.Width - m_Rectangle.Right;
            
            if(m_Rectangle.Top + deltaY < _snapMargin)
                deltaY = - m_Rectangle.Top;
            
            if(m_Rectangle.Bottom + deltaY > _containerSize.Height - _snapMargin)
                deltaY = _containerSize.Height - m_Rectangle.Bottom;
            
            Move(deltaX, deltaY);
        }
        private void MoveHandleKeepAspectRatio(Point point, int handleNumber, Size _originalSize)
        {
            
            // TODO: refactor/simplify.
            
            switch (handleNumber)
            {
                case 1:
                    {
                        // Top left handler.
                        int dx = point.X - m_Rectangle.Left;
                        int newWidth = m_Rectangle.Width - dx;

                        if (newWidth > m_MinimalSize.Width)
                        {
                            double qRatio = (double)newWidth / (double)_originalSize.Width;
                            int newHeight = (int)((double)_originalSize.Height * qRatio); 	// Only if square.

                            int newY = m_Rectangle.Top + m_Rectangle.Height - newHeight;

                            m_Rectangle = new Rectangle(point.X, newY, newWidth, newHeight);
                        }
                        break;
                    }
                case 2:
                    {

                        // Top right handler.
                        int dx = m_Rectangle.Right - point.X;
                        int newWidth = m_Rectangle.Width - dx;

                        if (newWidth > m_MinimalSize.Width)
                        {
                            double qRatio = (double)newWidth / (double)_originalSize.Width;
                            int newHeight = (int)((double)_originalSize.Height * qRatio); 	// Only if square.

                            int newY = m_Rectangle.Top + m_Rectangle.Height - newHeight;
                            int newX = point.X - newWidth;

                            m_Rectangle = new Rectangle(newX, newY, newWidth, newHeight);
                        }
                        break;
                    }
                case 3:
                    {
                        // Bottom right handler.
                        int dx = m_Rectangle.Right - point.X;
                        int newWidth = m_Rectangle.Width - dx;

                        if (newWidth > m_MinimalSize.Width)
                        {
                            double qRatio = (double)newWidth / (double)_originalSize.Width;
                            int newHeight = (int)((double)_originalSize.Height * qRatio); 	// Only if square.

                            int newY = m_Rectangle.Y;
                            int newX = point.X - newWidth;

                            m_Rectangle = new Rectangle(newX, newY, newWidth, newHeight);
                        }
                        break;
                    }
                case 4:
                    {
                        // Bottom left handler.
                        int dx = point.X - m_Rectangle.Left;
                        int newWidth = m_Rectangle.Width - dx;

                        if (newWidth > m_MinimalSize.Width)
                        {
                            double qRatio = (double)newWidth / (double)_originalSize.Width;
                            int newHeight = (int)((double)_originalSize.Height * qRatio); 	// Only if square.

                            int newY = m_Rectangle.Y;

                            m_Rectangle = new Rectangle(point.X, newY, newWidth, newHeight);
                        }
                        break;
                    }
                default:
                    break;
            }
        }
        private void MoveHandleFree(Point point, int handleNumber)
        {
            Rectangle target = Rectangle.Empty;
            
            switch (handleNumber)
            {
                case 1:
                    target = new Rectangle(point.X, point.Y, m_Rectangle.Right - point.X, m_Rectangle.Bottom - point.Y);
                    break;
                case 2:
                    target = new Rectangle(m_Rectangle.Left, point.Y, point.X - m_Rectangle.Left, m_Rectangle.Bottom - point.Y);
                    break;
                case 3:
                    target = new Rectangle(m_Rectangle.Left, m_Rectangle.Top, point.X - m_Rectangle.Left, point.Y - m_Rectangle.Top);
                    break;
                case 4:
                    target = new Rectangle(point.X, m_Rectangle.Top, m_Rectangle.Right - point.X, point.Y - m_Rectangle.Top);
                    break;
            }
            
            ApplyWithConstraints(target);
        }
        private void ApplyWithConstraints(Rectangle target)
        {
            if(target.Width < m_MinimalSize.Width)
                target = new Rectangle(m_Rectangle.Left, target.Top, m_MinimalSize.Width, target.Height);
            
            if(target.Height < m_MinimalSize.Height)
                target = new Rectangle(target.Left, m_Rectangle.Top, target.Width, m_MinimalSize.Height);
            
            m_Rectangle = target;
        }
    }
}
