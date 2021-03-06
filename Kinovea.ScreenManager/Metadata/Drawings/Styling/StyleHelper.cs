﻿#region License
/*
Copyright © Joan Charmant 2011.
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
using System.ComponentModel;
using System.ComponentModel.Design.Serialization;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Windows.Forms;

namespace Kinovea.ScreenManager
{
    /// <summary>
    /// A class to encapsulate the various styling primitive a drawing may need for rendering, 
    /// and provide some utility functions to get a Pen, Brush, Font or Color object according to client opacity or zoom.
    /// Typical drawing would use just two or three of the primitive for its decoration and leave the others undefined.
    /// 
    /// The primitives can be bound to a style element (editable in the UI) through the Bind() method on the 
    /// style element, passing the name of the primitive. The binding will be effective only if types are compatible.
    /// todo: example.
    /// </summary>
    /// <remarks>
    /// This class should merge and replace "LineStyle" and "InfoTextDecoration" classes.
    /// </remarks>
    public class StyleHelper
    {
        #region Exposed function delegates
        public BindWriter BindWrite;
        public BindReader BindRead;
        
        /// <summary>
        /// Event raised when the value is changed dynamically through binding.
        /// This may be useful if the Drawing has several StyleHelper that must be linked somehow.
        /// An example use is when we change the main color of the track, we need to propagate the change
        /// to the small label attached (for the Label following mode).
        /// </summary>
        /// <remarks>The event is not raised when the value is changed manually through a property setter</remarks>
        public event EventHandler ValueChanged;
        #endregion
        
        #region Properties
        public Color Color
        {
            get { return color; }
            set { color = value; }
        }
        public int LineSize
        {
            get { return lineSize; }
            set { lineSize = value;}
        }
        public LineShape LineShape
        {
            get { return lineShape; }
            set { lineShape = value; }
        }
        public LineEnding LineEnding
        {
            get { return lineEnding; }
            set { lineEnding = value;}
        }
        public bool Curved
        {
            get { return curved; }
            set { curved = value; }
        }
        public Font Font
        {
            get { return font; }
            set 
            { 
                if(value != null)
                {
                    // We make temp copies of the variables because we call .Dispose() but 
                    // it's possible that input value was pointing to the same reference.
                    string fontName = value.Name;
                    FontStyle fontStyle = value.Style;
                    float fontSize = value.Size;
                    font.Dispose();
                    font = new Font(fontName, fontSize, fontStyle);
                }
                else
                {
                    font.Dispose();
                    font = null;
                }
            }
        }
        public Bicolor Bicolor
        {
            get { return bicolor; }
            set { bicolor = value; }
        }
        public TrackShape TrackShape
        {
            get { return trackShape; }
            set { trackShape = value;}
        }
        public int GridDivisions
        {
            get { return gridDivisions;}
            set { gridDivisions = value;}
        }
        public int ContentHash
        {
            get 
            {
                int iHash = 0;
                
                iHash ^= color.GetHashCode();
                iHash ^= lineSize.GetHashCode();
                iHash ^= font.GetHashCode();
                iHash ^= bicolor.ContentHash;
                iHash ^= lineEnding.GetHashCode();
                iHash ^= trackShape.GetHashCode();
                iHash ^= curved.GetHashCode();
                
                return iHash;
            }
        }
        #endregion
        
        #region Members
        private Color color;
        private int lineSize;
        private LineShape lineShape;
        private Font font = new Font("Arial", 12, FontStyle.Regular);
        private Bicolor bicolor;
        private LineEnding lineEnding = LineEnding.None;
        private TrackShape trackShape = TrackShape.Solid;
        private bool curved;
        private int gridDivisions;
        
        // Internal only
        private static readonly int[] allowedFontSizes = { 8, 9, 10, 11, 12, 14, 16, 18, 20, 24, 28, 32, 36 };
        private static readonly log4net.ILog log = log4net.LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);
        #endregion
        
        #region Constructor
        public StyleHelper()
        {
            BindWrite = DoBindWrite;
            BindRead = DoBindRead;
        }
        #endregion
        
        #region Public Methods
        
        #region Color and LineSize properties
        /// <summary>
        /// Returns a Pen object suitable to draw a background or color only contour.
        /// The pen object will only integrate the color property and be of width 1.
        /// </summary>
        /// <param name="alpha">Alpha value to multiply the color with</param>
        /// <returns>Pen object initialized with the current value of color and width = 1.0</returns>
        public Pen GetPen(int alpha)
        {
            Color c = (alpha >= 0 && alpha <= 255) ? Color.FromArgb(alpha, color) : color;
            
            return NormalPen(new Pen(c, 1.0f));
        }
        public Pen GetPen(double opacity)
        {
            return GetPen((int)(opacity * 255));
        }

        /// <summary>
        /// Returns a Pen object suitable to draw a line or contour.
        /// The pen object will integrate the color, line size.
        /// Line shape is drawn in the drawing to accomodate for squiggly lines.
        /// Line ending is drawn in the drawing to have better arrows that what is provided by the Pen class.
        /// </summary>
        /// <param name="alpha">Alpha value to multiply the color with</param>
        /// <param name="stretchFactor">zoom value to multiply the line size with</param>
        /// <returns>Pen object initialized with the current value of color and line size properties</returns>
        public Pen GetPen(int alpha, double stretchFactor)
        {
            Color c = (alpha >= 0 && alpha <= 255) ? Color.FromArgb(alpha, color) : color;
            float penWidth = (float)((double)lineSize * stretchFactor);
            if (penWidth < 1) 
                penWidth = 1;
            
            Pen p = new Pen(c, penWidth);
            p.LineJoin = LineJoin.Round;
            
            p.DashStyle = trackShape.DashStyle;
            
            return p;
        }
        public Pen GetPen(double opacity, double stretchFactor)
        {
            return GetPen((int)(opacity * 255), stretchFactor);
        }
        
        /// <summary>
        /// Returns a Brush object suitable to draw a background or colored area.
        /// Only use the color property.
        /// </summary>
        /// <param name="alpha">Alpha value to multiply the color with</param>
        /// <returns>Brush object initialized with the current value of color property</returns>
        public SolidBrush GetBrush(int alpha)
        {
            Color c = (alpha >= 0 && alpha <= 255) ? Color.FromArgb(alpha, color) : color;
            return new SolidBrush(c);
        }
        public SolidBrush GetBrush(double opacity)
        {
            return GetBrush((int)(opacity * 255));
        }
        #endregion
        
        #region Font property
        public Font GetFont(float stretchFactor)
        {
            float fFontSize = GetRescaledFontSize(stretchFactor);
            return new Font(font.Name, fFontSize, font.Style);
        }
        public Font GetFontDefaultSize(int fontSize)
        {
            return new Font(font.Name, fontSize, font.Style);
        }
        public void ForceFontSize(int wantedHeight, String text)
        {
            // Compute the optimal font size from a given background rectangle.
            // This is used when the user drag the bottom right corner to resize the text.
            // _wantedHeight is unscaled.
            Button but = new Button();
            Graphics g = but.CreateGraphics();

            // We must loop through all allowed font size and compute the output rectangle to find the best match.
            // We only compare with wanted height for simplicity.
            int smallestDiff = int.MaxValue;
            int bestCandidate = allowedFontSizes[0];
            
            foreach(int size in allowedFontSizes)
            {
                Font testFont = new Font(font.Name, size, font.Style);
                SizeF bgSize = g.MeasureString(text + " ", testFont);
                testFont.Dispose();
                
                int diff = (int)Math.Abs(wantedHeight - (int)bgSize.Height);
                
                if(diff < smallestDiff)
                {
                    smallestDiff = diff;
                    bestCandidate = size;
                }
            }
            
            g.Dispose();
            
            // Push to internal value.
            string fontName = font.Name;
            FontStyle fontStyle = font.Style;
            font.Dispose();
            font = new Font(fontName, bestCandidate, fontStyle);
        }
        #endregion
        
        #region Bicolor property
        public Color GetForegroundColor(int alpha)
        {
            Color c = (alpha >= 0 && alpha <= 255) ? Color.FromArgb(alpha, bicolor.Foreground) : bicolor.Foreground;
            return c;
        }
        public SolidBrush GetForegroundBrush(int alpha)
        {
            Color c = GetForegroundColor(alpha);
            return new SolidBrush(c);
        }
        public Pen GetForegroundPen(int alpha)
        {
            Color c = GetForegroundColor(alpha);
            return NormalPen(new Pen(c, 1.0f));
        }
        public Color GetBackgroundColor(int alpha)
        {
            Color c = (alpha >= 0 && alpha <= 255) ? Color.FromArgb(alpha, bicolor.Background) : bicolor.Background;
            return c;
        }
        public SolidBrush GetBackgroundBrush(int alpha)
        {
            Color c = GetBackgroundColor(alpha);
            return new SolidBrush(c);
        }
        public Pen GetBackgroundPen(int alpha)
        {
            Color c = GetBackgroundColor(alpha);
            return NormalPen(new Pen(c, 1.0f));
        }
        #endregion
        
        #endregion
        
        #region Private Methods
        private void DoBindWrite(string targetProperty, object value)
        {
            // Check type and import value if compatible with the target prop.
            bool imported = false;
            switch (targetProperty)
            {
                case "Color":
                    {
                        if (value is Color)
                        {
                            color = (Color)value;
                            imported = true;
                        }
                        break;
                    }
                case "LineSize":
                    {
                        if (value is int)
                        {
                            lineSize = (int)value;
                            imported = true;
                        }

                        break;
                    }
                case "LineShape":
                    {
                        if (value is LineShape)
                        {
                            lineShape = (LineShape)value;
                            imported = true;
                        }

                        break;
                    }
                case "LineEnding":
                    {
                        if (value is LineEnding)
                        {
                            lineEnding = (LineEnding)value;
                            imported = true;
                        }

                        break;
                    }
                case "TrackShape":
                    {
                        if (value is TrackShape)
                        {
                            trackShape = (TrackShape)value;
                            imported = true;
                        }

                        break;
                    }
                case "Curved":
                    {
                        if (value is Boolean)
                        {
                            curved = (Boolean)value;
                            imported = true;
                        }

                        break;
                    }
                case "Font":
                    {
                        if (value is int)
                        {
                            // Recreate the font changing just the size.
                            string fontName = font.Name;
                            FontStyle fontStyle = font.Style;
                            font.Dispose();
                            font = new Font(fontName, (int)value, fontStyle);
                            imported = true;
                        }
                        break;
                    }
                case "Bicolor":
                    {
                        if (value is Color)
                        {
                            bicolor.Background = (Color)value;
                            imported = true;
                        }
                        break;
                    }
                case "GridDivisions":
                    {
                        if (value is int)
                        {
                            gridDivisions = (int)value;
                            imported = true;
                        }
                        break;
                    }
                default:
                    {
                        log.DebugFormat("Unknown target property \"{0}\".", targetProperty);
                        break;
                    }
            }
            
            if(imported)
            {
                if(ValueChanged != null) 
                    ValueChanged(null, EventArgs.Empty);
            }
            else
            {
                log.DebugFormat("Could not import value \"{0}\" to property \"{1}\"." , value.ToString(), targetProperty);
            }
            
        }
        private object DoBindRead(string sourceProperty, Type targetType)
        {
            // Take the local property and extract something of the required type.
            // This function is used by style elements to stay up to date in case the bound property has been modified externally.
            // The style element might be of an entirely different type than the property.
            bool converted = false;
            object result = null;
            switch (sourceProperty)
            {
                case "Color":
                    {
                        if (targetType == typeof(Color))
                        {
                            result = color;
                            converted = true;
                        }
                        break;
                    }
                case "LineSize":
                    {
                        if (targetType == typeof(int))
                        {
                            result = lineSize;
                            converted = true;
                        }
                        break;
                    }
                case "LineShape":
                    {
                        if (targetType == typeof(LineShape))
                        {
                            result = lineShape;
                            converted = true;
                        }
                        break;
                    }
                case "LineEnding":
                    {
                        if (targetType == typeof(LineEnding))
                        {
                            result = lineEnding;
                            converted = true;
                        }
                        break;
                    }
                case "TrackShape":
                    {
                        if (targetType == typeof(TrackShape))
                        {
                            result = trackShape;
                            converted = true;
                        }
                        break;
                    }
                case "Curved":
                    {
                        if (targetType == typeof(Boolean))
                        {
                            result = curved;
                            converted = true;
                        }

                        break;
                    }
                case "Font":
                    {
                        if (targetType == typeof(int))
                        {
                            result = (int)font.Size;
                            converted = true;
                        }
                        break;
                    }
                case "Bicolor":
                    {
                        if (targetType == typeof(Color))
                        {
                            result = bicolor.Background;
                            converted = true;
                        }
                        break;
                    }
                case "GridDivisions":
                    {
                        if (targetType == typeof(int))
                        {
                            result = gridDivisions;
                            converted = true;
                        }
                        break;
                    }
                default:
                    {
                        log.DebugFormat("Unknown source property \"{0}\".", sourceProperty);
                        break;
                    }
            }
            
            if(!converted)
            {
                log.DebugFormat("Could not convert property \"{0}\" to update value \"{1}\"." , sourceProperty, targetType);
            }
            
            return result;
        }
        private float GetRescaledFontSize(float stretchFactor)
        {
            // Get the strecthed font size.
            // The final font size returned here may not be part of the allowed font sizes
            // and may exeed the max allowed font size, because it's just for rendering purposes.
            float fontSize = (float)(font.Size * stretchFactor);
            if(fontSize < 8) 
                fontSize = 8;
            
            return fontSize;
        }
        private Pen NormalPen(Pen p)
        {
            p.StartCap = LineCap.Round;
            p.EndCap = LineCap.Round;
            p.LineJoin = LineJoin.Round;
            return p;
        }
        #endregion
    }

    /// <summary>
    /// A simple wrapper around two color values.
    /// When setting the background color, the foreground color is automatically adjusted 
    /// to black or white depending on the luminosity of the background color.
    /// </summary>
    public struct Bicolor
    {
        public Color Foreground
        {
            get { return foreground;}
        }
        public Color Background
        {
            get { return background;}
            set 
            { 
                background = value;
                foreground = value.GetBrightness() >= 0.5  ? Color.Black : Color.White;
            }
        }
        public int ContentHash
        {
            get 
            {
                return background.GetHashCode() ^ foreground.GetHashCode();
            }
        }
        
        private Color foreground;
        private Color background;
        
        public Bicolor(Color backColor)
        {
            background = backColor;
            foreground = backColor.GetBrightness() >= 0.5  ? Color.Black : Color.White;
        }
    }
    
    
    
    
        
}
