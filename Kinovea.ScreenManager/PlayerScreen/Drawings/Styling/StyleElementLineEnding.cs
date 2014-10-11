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
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using System.Xml;

using Kinovea.ScreenManager.Languages;

namespace Kinovea.ScreenManager
{
    /// <summary>
    /// Style element to represent line endings (for arrows).
    /// Editor: owner drawn combo box.
    /// </summary>
    public class StyleElementLineEnding : AbstractStyleElement
    {
        #region Properties
        public static readonly LineEnding[] Options = { LineEnding.None, LineEnding.StartArrow, LineEnding.EndArrow, LineEnding.DoubleArrow };
        public override object Value
        {
            get { return lineEnding; }
            set 
            { 
                lineEnding = (value is LineEnding) ? (LineEnding)value : LineEnding.None;
                RaiseValueChanged();
            }
        }
        public override Bitmap Icon
        {
            get { return Properties.Drawings.arrows;}
        }
        public override string DisplayName
        {
            get { return ScreenManagerLang.Generic_ArrowPicker;}
        }
        public override string XmlName
        {
            get { return "Arrows";}
        }
        #endregion
        
        #region Members
        private LineEnding lineEnding;
        private static readonly int lineWidth = 6;
        private static readonly log4net.ILog log = log4net.LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);
        #endregion
        
        #region Constructor
        public StyleElementLineEnding(LineEnding defaultValue)
        {
            lineEnding = (Array.IndexOf(Options, defaultValue) >= 0) ? defaultValue : LineEnding.None;
        }
        public StyleElementLineEnding(XmlReader xmlReader)
        {
            ReadXML(xmlReader);
        }
        #endregion
        
        #region Public Methods
        public override Control GetEditor()
        {
            ComboBox editor = new ComboBox();
            editor.DropDownStyle = ComboBoxStyle.DropDownList;
            editor.ItemHeight = 15;
            editor.DrawMode = DrawMode.OwnerDrawFixed;
            for(int i=0;i<Options.Length;i++) 
                editor.Items.Add(new object());
            
            editor.SelectedIndex = Array.IndexOf(Options, lineEnding);
            editor.DrawItem += new DrawItemEventHandler(editor_DrawItem);
            editor.SelectedIndexChanged += new EventHandler(editor_SelectedIndexChanged);
            return editor;
        }
        public override AbstractStyleElement Clone()
        {
            AbstractStyleElement clone = new StyleElementLineEnding(lineEnding);
            clone.Bind(this);
            return clone;
        }
        public override void ReadXML(XmlReader xmlReader)
        {
            xmlReader.ReadStartElement();
            string s = xmlReader.ReadElementContentAsString("Value", "");
            
            LineEnding value = LineEnding.None;
            try
            {
                TypeConverter lineEndingConverter = TypeDescriptor.GetConverter(typeof(LineEnding));
                value = (LineEnding)lineEndingConverter.ConvertFromString(s);
            }
            catch(Exception)
            {
                log.ErrorFormat("An error happened while parsing XML for Line ending. {0}", s);
            }
            
            // Restrict to the actual list of "athorized" values.
            lineEnding = (Array.IndexOf(Options, value) >= 0) ? value : LineEnding.None;
            
            xmlReader.ReadEndElement();
        }
        public override void WriteXml(XmlWriter xmlWriter)
        {
            TypeConverter converter = TypeDescriptor.GetConverter(lineEnding);
            string s = converter.ConvertToString(lineEnding);
            xmlWriter.WriteElementString("Value", s);
        }
        #endregion
        
        #region Private Methods
        private void editor_DrawItem(object sender, DrawItemEventArgs e)
        {
            if (e.Index < 0 || e.Index >= Options.Length)
                return;
            
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            int top = e.Bounds.Height / 2;
                
            Pen p = new Pen(Color.Black, lineWidth);
            switch(Options[e.Index])
            {
                case LineEnding.None:
                    e.Graphics.DrawLine(p, e.Bounds.Left, e.Bounds.Top + top, e.Bounds.Left + e.Bounds.Width, e.Bounds.Top + top);
                    break;
                case LineEnding.StartArrow:
                    p.StartCap = LineCap.ArrowAnchor;
                    e.Graphics.DrawLine(p, e.Bounds.Left, e.Bounds.Top + top, e.Bounds.Left + e.Bounds.Width, e.Bounds.Top + top);
                    break;
                case LineEnding.EndArrow:
                    p.EndCap = LineCap.ArrowAnchor;
                    e.Graphics.DrawLine(p, e.Bounds.Left, e.Bounds.Top + top, e.Bounds.Left + e.Bounds.Width, e.Bounds.Top + top);
                    break;
                case LineEnding.DoubleArrow:
                    p.StartCap = LineCap.ArrowAnchor;
                    p.EndCap = LineCap.ArrowAnchor;
                    e.Graphics.DrawLine(p, e.Bounds.Left, e.Bounds.Top + top, e.Bounds.Left + e.Bounds.Width, e.Bounds.Top + top);
                    break;
            }
            
            p.Dispose();
        }
        private void editor_SelectedIndexChanged(object sender, EventArgs e)
        {
            int index = ((ComboBox)sender).SelectedIndex;
            if( index >= 0 && index < Options.Length)
            {
                lineEnding = Options[index];
                RaiseValueChanged();
            }
        }
        #endregion
    }
}
