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
#region License
/*
Copyright � Joan Charmant 2009.
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
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Text;
using System.Windows.Forms;
using System.IO;
using System.Threading;
using System.Resources;
using System.Reflection;
using Videa.Services;

namespace Videa.ScreenManager
{
	
	/// <summary>
	/// This form lets the user choose how many images will be visible when in mosaic mode.
	/// The user can also choose to view only keyframes.
	/// 
	/// On cancel, the internal state of the mosaic object given as parameter should not have moved. 
	/// </summary>
    public partial class formConfigureMosaic : Form
    {
        #region Members

        private PlayerScreenUserInterface m_PlayerScreenUserInterface;      // parent
        private Mosaic m_Mosaic;
        private Metadata m_Metadata;
        private bool m_AnalysisMode;
        
        private double m_fDurationInSeconds;
        private int m_iDurationinFrames;
        
        private static readonly int m_iDefaultFramesToExtract = 25;
        private int m_iFramesToExtract = 25;
        private ResourceManager m_ResourceManager;
        
        #endregion

        public formConfigureMosaic(PlayerScreenUserInterface _psui, Mosaic _mosaic, Metadata _metadata, bool _bAnalysisMode, Int64 _iSelDuration, double _tsps, double _fps)
        {
            m_PlayerScreenUserInterface = _psui;
            m_Mosaic = _mosaic;
            m_Metadata = _metadata;
            m_AnalysisMode = _bAnalysisMode;
            
            m_fDurationInSeconds = _iSelDuration / _tsps;
            m_iDurationinFrames = (int)(m_fDurationInSeconds * _fps);

            m_ResourceManager = new ResourceManager("Videa.ScreenManager.Languages.ScreenManagerLang", Assembly.GetExecutingAssembly());

            InitializeComponent();
            
            SetupUICulture();
            SetupData();
            UpdateLabels();
        }
        private void SetupUICulture()
        {
            // Window
            this.Text = "   " + m_ResourceManager.GetString("dlgConfigureMosaic_Title", Thread.CurrentThread.CurrentUICulture);
            
            // Group Config
            grpboxConfig.Text = m_ResourceManager.GetString("Generic_Configuration", Thread.CurrentThread.CurrentUICulture);
            rbKeyframes.Text = m_ResourceManager.GetString("dlgConfigureMosaic_radioKeyframes", Thread.CurrentThread.CurrentUICulture);
            rbFrequency.Text = m_ResourceManager.GetString("dlgConfigureMosaic_radioFrequency", Thread.CurrentThread.CurrentUICulture);
            cbRTL.Text = m_ResourceManager.GetString("dlgConfigureMosaic_cbRightToLeft", Thread.CurrentThread.CurrentUICulture);
            
            // Buttons
            btnOK.Text = m_ResourceManager.GetString("Generic_Apply", Thread.CurrentThread.CurrentUICulture);
            btnCancel.Text = m_ResourceManager.GetString("Generic_Cancel", Thread.CurrentThread.CurrentUICulture);
        }
        private void SetupData()
        {
        	if(m_Mosaic.KeyImagesOnly && m_PlayerScreenUserInterface.Metadata.Count > 0)
        	{
        		rbKeyframes.Checked = true;
        		rbFrequency.Checked = false;
        	}
        	else
        	{
        		rbKeyframes.Checked = false;
        		rbFrequency.Checked = true;	
        		        		
        		rbKeyframes.Enabled = m_PlayerScreenUserInterface.Metadata.Count > 0;
        	}
        	
        	cbRTL.Checked = m_Mosaic.RightToLeft;
        	
        	if(m_iDurationinFrames < trkInterval.Minimum)
        	{
        		// Error.
        	}
        	
        	if(m_iDurationinFrames < trkInterval.Maximum)
        	{
        		trkInterval.Maximum = m_iDurationinFrames;
        	}
        	
        	if(m_Mosaic.LastImagesCount >= trkInterval.Minimum && m_Mosaic.LastImagesCount <= trkInterval.Maximum)
        	{
        		trkInterval.Value = m_Mosaic.LastImagesCount;
        	}
        	else if(m_iDefaultFramesToExtract <= trkInterval.Maximum)
        	{
        		trkInterval.Value = m_iDefaultFramesToExtract;
        	}
        	else
        	{
        		trkInterval.Value = trkInterval.Maximum;
        	}
        }
        private void RbFrequencyCheckedChanged(object sender, EventArgs e)
        {
        	trkInterval.Enabled = rbFrequency.Checked;
        	lblInfosTotalFrames.Enabled = rbFrequency.Checked;
        	lblInfosFrequency.Enabled = rbFrequency.Checked;
        }
        private void trkInterval_ValueChanged(object sender, EventArgs e)
        {
        	int iRoot = (int)(Math.Sqrt((double)trkInterval.Value));
        	m_iFramesToExtract = iRoot * iRoot;
            UpdateLabels();
        }
        private void UpdateLabels()
        {
        	// Number of frames
            lblInfosTotalFrames.Text = String.Format(m_ResourceManager.GetString("dlgConfigureMosaic_LabelImages", Thread.CurrentThread.CurrentUICulture), " " + m_iFramesToExtract);            
        	
            // Frequency
            double fInterval = m_fDurationInSeconds / (double)m_iFramesToExtract;
            lblInfosFrequency.Text = m_ResourceManager.GetString("dlgConfigureMosaic_LabelFrequencyRoot", Thread.CurrentThread.CurrentUICulture) + " ";
            if (fInterval < 1)
            {
                int iHundredth = (int)(fInterval * 100);
                lblInfosFrequency.Text += String.Format(m_ResourceManager.GetString("dlgRafaleExport_LabelFrequencyHundredth", Thread.CurrentThread.CurrentUICulture), iHundredth);
            }
            else
            {
                lblInfosFrequency.Text += String.Format(m_ResourceManager.GetString("dlgRafaleExport_LabelFrequencySeconds", Thread.CurrentThread.CurrentUICulture), fInterval);
            }
        }
        private void btnOK_Click(object sender, EventArgs e)
        {   
        	Hide();
        	
        	m_Mosaic.RightToLeft = cbRTL.Checked;
        	
        	if(rbKeyframes.Checked)
        	{
        		m_Mosaic.KeyImagesOnly = true;
        		m_Mosaic.Load(m_Metadata.GetFullImages());
        	}
        	else if(m_AnalysisMode)
        	{
        		// Frames should be available right away from the player server.
        		m_Mosaic.KeyImagesOnly = false;
        		m_Mosaic.Load(m_PlayerScreenUserInterface.m_PlayerServer.ExtractForMosaic(m_iFramesToExtract));
        	}
        	else
        	{
        		// Display the progress bar and launch the process of getting the images.
        		// this will need a full parse of the video.
        		
        		//formFramesMosaic ffm = new formFramesMosaic(m_PlayerScreenUserInterface, m_iEstimatedTotal);
            	//ffm.ShowDialog();
            	//ffm.Dispose();
        	}
                    
            Close();
        }
        
    }
}