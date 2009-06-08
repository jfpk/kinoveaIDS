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
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Text;
using System.Windows.Forms;
using System.Diagnostics;
using System.Resources;
using System.Threading;
using Videa.Services;

namespace Videa.Root
{
    public partial class FormAbout : Form
    {
        public FormAbout(ResourceManager resManager)
        {
            InitializeComponent();
            if (resManager != null) 
            { 
                this.Text = "   " + resManager.GetString("mnuAbout", Thread.CurrentThread.CurrentUICulture);

                rtbInfos.Clear();
                rtbInfos.AppendText(resManager.GetString("dlgAbout_Info1", Thread.CurrentThread.CurrentUICulture).Replace("\\n", "\n"));
                rtbInfos.AppendText(resManager.GetString("dlgAbout_Info3", Thread.CurrentThread.CurrentUICulture).Replace("\\n", "\n"));
                rtbInfos.AppendText(resManager.GetString("dlgAbout_Info4", Thread.CurrentThread.CurrentUICulture).Replace("\\n", "\n"));

                rtbInfos.AppendText(" Nederlands - Peter Strikwerda.\n");
                rtbInfos.AppendText(" Deutsch - Stephan Frost, Dominique Saussereau, Jonathan Boder.\n");
                rtbInfos.AppendText(" Português - Fernando Jorge, Rafael Fernandes.\n");
                rtbInfos.AppendText(" Español - Rafael Gonzalez, Lionel Sosa Estrada.\n");
                rtbInfos.AppendText(" Italiano - Giorgio Biancuzzi.\n");
                rtbInfos.AppendText(" Română - Bogdan Paul Frăţilă.\n");

                rtbInfos.AppendText(resManager.GetString("dlgAbout_Info2", Thread.CurrentThread.CurrentUICulture).Replace("\\n", "\n"));

                rtbInfos.AppendText(" FFmpeg - Video formats and codecs library - The FFmpeg contributors.\n");
                rtbInfos.AppendText(" AForge - .NET Image processing library - Andrew Kirillov.\n");
                rtbInfos.AppendText(" ExpTree - .NET File Explorer control - Jim Parsells.\n");
                //rtbInfos.AppendText(" 3DPlot - 3D .NET Library - Pete Everett.\n");
                rtbInfos.AppendText(" FileDownloader - Phil Crosby.\n");
                rtbInfos.AppendText(" PDFSharp - Empira Software.\n");
                rtbInfos.AppendText(" log4Net - Apache Foundation.\n");
            }

            labelCopyright.Text = "Copyright © 2006-2009 - Joan Charmant";
            lblKinovea.Text = "Kinovea - " + PreferencesManager.ReleaseVersion;

            // Lien internet
            lnkKinovea.Links.Clear();
            lnkKinovea.Links.Add(0, lnkKinovea.Text.Length, "http://www.kinovea.org");
        }

        private void lnkKinovea_LinkClicked(object sender, LinkLabelLinkClickedEventArgs e)
        {
            // Launch default web browser
            ProcessStartInfo sInfo = new ProcessStartInfo(e.Link.LinkData.ToString());
            Process.Start(sInfo);
        }
    }
}