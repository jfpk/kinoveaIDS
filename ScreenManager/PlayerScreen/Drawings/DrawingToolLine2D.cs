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
using System.Drawing;
using System.Windows.Forms;

using Kinovea.ScreenManager.Languages;

namespace Kinovea.ScreenManager
{
    public class DrawingToolLine2D : AbstractDrawingTool
    {
        #region Properties
        public override string DisplayName
        {
            get { return ScreenManagerLang.ToolTip_DrawingToolLine2D; }
        }
        public override Bitmap Icon
        {
            get { return Properties.Drawings.line; }
        }
        public override bool Attached
        {
            get { return true; }
        }
        public override bool KeepTool
        {
            get { return true; }
        }
        public override bool KeepToolFrameChanged
        {
            get { return true; }
        }
        public override DrawingStyle StylePreset
        {
            get { return m_StylePreset;}
            set { m_StylePreset = value;}
        }
        public override DrawingStyle DefaultStylePreset
        {
            get { return m_DefaultStylePreset;}
        }
        #endregion
        
        #region Members
        private DrawingStyle m_DefaultStylePreset = new DrawingStyle();
        private DrawingStyle m_StylePreset;
        #endregion
        
        #region Constructor
        public DrawingToolLine2D()
        {
            m_DefaultStylePreset.Elements.Add("color", new StyleElementColor(Color.LightGreen));
            m_DefaultStylePreset.Elements.Add("line size", new StyleElementLineSize(2));
            m_DefaultStylePreset.Elements.Add("arrows", new StyleElementLineEnding(LineEnding.None));
            m_StylePreset = m_DefaultStylePreset.Clone();
        }
        #endregion
        
        #region Public Methods
        public override AbstractDrawing GetNewDrawing(Point _Origin, long _iTimestamp, long _AverageTimeStampsPerFrame)
        {
            return new DrawingLine2D(_Origin, new Point(_Origin.X + 10, _Origin.Y), _iTimestamp, _AverageTimeStampsPerFrame, m_StylePreset);
        }
        public override Cursor GetCursor(double _fStretchFactor)
        {
            return Cursors.Cross;
        }
        #endregion
    }
}
