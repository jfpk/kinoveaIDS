﻿#region License
/*
Copyright © Joan Charmant 2014.
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
using System.Threading;

namespace Kinovea.Camera.FrameGenerator
{
    /// <summary>
    /// Retrieve a single snapshot, simulating a synchronous function. Used for thumbnails.
    /// </summary>
    public class SnapshotRetriever
    {
        public event EventHandler<CameraImageReceivedEventArgs> CameraImageReceived;
        public event EventHandler CameraImageTimedOut;
        public event EventHandler CameraImageError;

        public string Identifier
        {
            get { return this.summary.Identifier; }
        }

        public string Error
        {
            get { return "Unknown error"; }
        }

        #region Members
        private Bitmap image;
        private CameraSummary summary;
        private static readonly log4net.ILog log = log4net.LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);
        #endregion

        public SnapshotRetriever(CameraManagerFrameGenerator manager, CameraSummary summary)
        {
            this.summary = summary;
        }

        public void Run(object data)
        {
            Generator generator = new Generator();
            image = generator.Generate(new Size(640, 480));
            
            if (CameraImageReceived != null)
                CameraImageReceived(this, new CameraImageReceivedEventArgs(summary, image));
        }

        public void Cancel()
        {
        }
    }
}

