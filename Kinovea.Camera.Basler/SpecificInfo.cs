﻿#region License
/*
Copyright © Joan Charmant 2013.
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
using System.Drawing;
using PylonC.NET;

namespace Kinovea.Camera.Basler
{
    /// <summary>
    /// Information about a camera that is specific to the Basler plugin.
    /// This info is opaquely transported inside the CameraSummary.
    /// </summary>
    public class SpecificInfo
    {
        public PYLON_DEVICE_HANDLE Handle { get; set; }
    }
}
