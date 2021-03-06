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
    public class GenericPostureAngle
    {
        public int Origin { get; private set;}
        public int Leg1 { get; private set;}
        public int Leg2 { get; private set;}
        public bool Relative { get; private set;}
        public int Radius { get; set;}
        public bool Tenth { get; private set;}
        public string Symbol { get; private set;}
        public Color Color { get; private set; }
        public string OptionGroup { get; private set;}
        
        public GenericPostureAngle(XmlReader r)
        {
            //<Angle origin="1" leg1="2" leg2="3" relative="true" />
            
            Relative = true;
            Color = Color.Transparent;
            OptionGroup = "";
            
            bool isEmpty = r.IsEmptyElement;
            
            if(r.MoveToAttribute("origin"))
                Origin = r.ReadContentAsInt();
            
            if(r.MoveToAttribute("leg1"))
                Leg1 = r.ReadContentAsInt();

            if(r.MoveToAttribute("leg2"))
                Leg2 = r.ReadContentAsInt();
            
            if(r.MoveToAttribute("relative"))
                Relative = XmlHelper.ParseBoolean(r.ReadContentAsString());

            if(r.MoveToAttribute("tenth"))
                Tenth = XmlHelper.ParseBoolean(r.ReadContentAsString());
            
            if(r.MoveToAttribute("radius"))
                Radius = r.ReadContentAsInt();
            
            if(r.MoveToAttribute("symbol"))
                Symbol = r.ReadContentAsString();
                
            if(r.MoveToAttribute("color"))
                Color = XmlHelper.ParseColor(r.ReadContentAsString(), Color);
            
            if(r.MoveToAttribute("optionGroup"))
                OptionGroup = r.ReadContentAsString();

            r.ReadStartElement();
            
            if(isEmpty)
                return;
            
            // Read sub elements.
            //r.ReadEndElement();
        }
    }
}