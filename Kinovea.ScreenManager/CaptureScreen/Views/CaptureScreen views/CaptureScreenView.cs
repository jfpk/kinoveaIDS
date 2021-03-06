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
using System.ComponentModel;
using System.Drawing;
using System.Windows.Forms;
using System.Linq;

using Kinovea.ScreenManager.Languages;
using Kinovea.Services;

namespace Kinovea.ScreenManager
{
    /// <summary>
    /// This is the main implementation of ICaptureScreenView.
    /// This implementation is used in the actual capture screens.
    /// </summary>
    public partial class CaptureScreenView : KinoveaControl, ICaptureScreenView
    {
        #region Properties
        public string CurrentImageFilename
        {
            get { return fnbImage.Filename; }
        }

        public string CurrentVideoFilename
        {
            get { return fnbVideo.Filename; }
        }
        #endregion

        #region Events
        public event EventHandler<EventArgs<HotkeyCommand>> DualCommandReceived;
        #endregion

        #region Members
        private CaptureScreen presenter;
        private CapturedFilesView capturedFilesView;
        private bool recording;
        private bool grabbing;
        #endregion
        
        public CaptureScreenView(CaptureScreen presenter)
        {
            InitializeComponent();
            lblCameraTitle.Text = "";
            lblCameraInfo.Text = "";
            this.presenter = presenter;
            ToggleCapturedVideosPanel();
            sldrDelay.ValueChanged += SldrDelay_ValueChanged;
            this.Hotkeys = HotkeySettingsManager.LoadHotkeys("CaptureScreen");
        }

        #region Public methods
        
        #region ICaptureScreenView
        public void DisplayAsActiveScreen(bool active)
        {
        
        }
        
        public void FullScreen(bool fullScreen)
        {
        
        }
        
        public void RefreshUICulture()
        {
            capturedFilesView.RefreshUICulture();
            UpdateDelayLabel(0, 0);
            ReloadTooltipsCulture();
        }
        
        public void AddImageDrawing(string filename, bool svg)
        {
        
        }
        
        public void AddImageDrawing(Bitmap bmp)
        {
        
        }
        
        public void BeforeClose()
        {
        
        }
        
        public void SetViewport(Viewport viewport)
        {
            pnlViewport.Controls.Add(viewport);
            viewport.Dock = DockStyle.Fill;
        }
        public void SetCapturedFilesView(CapturedFilesView capturedFilesView)
        {
            this.capturedFilesView = capturedFilesView;
            pnlCapturedVideos.Controls.Add(capturedFilesView);
            capturedFilesView.Dock = DockStyle.Fill;
        }
        public void SetToolbarView(Control toolbar)
        {
            pnlDrawingToolsBar.Controls.Add(toolbar);
        }
        
        public void UpdateTitle(string title, Bitmap icon)
        {
            btnIcon.BackgroundImage = icon;
            lblCameraTitle.Text = title;
            lblCameraInfo.Left = lblCameraTitle.Right + 5;
        }
        
        public void UpdateInfo(string info)
        {
            lblCameraInfo.Text = info;
        }
        
        public void UpdateGrabbingStatus(bool grabbing)
        {
            this.grabbing = grabbing;

            if (grabbing)
            {
                btnGrab.Image = Properties.Capture.grab_pause;
                toolTips.SetToolTip(btnGrab, ScreenManagerLang.ToolTip_PauseCamera);
            }
            else
            {
                btnGrab.Image = Properties.Capture.grab_start;
                toolTips.SetToolTip(btnGrab, ScreenManagerLang.ToolTip_StartCamera);
            }
        }
        public void UpdateRecordingStatus(bool recording)
        {
            this.recording = recording;

            if(recording)
            {
                btnRecord.Image = Properties.Capture.record_stop;
                toolTips.SetToolTip(btnRecord, ScreenManagerLang.ToolTip_StopRecording);
            }
            else
            {
                btnRecord.Image = Properties.Capture.record_start;
                toolTips.SetToolTip(btnRecord, ScreenManagerLang.ToolTip_StartRecording);
            }
            
            btnSettings.Enabled = !recording;
            fnbImage.Enabled = !recording;
            fnbVideo.Enabled = !recording;
        }
        public void UpdateDelayLabel(double delaySeconds, int delayFrames)
        {
            double round = Math.Round(delaySeconds);

            string formattedDelay = "";
            if (round < 10)
                formattedDelay = string.Format("{0:0.00}", delaySeconds);
            else
                formattedDelay = string.Format("{0}", round);

            lblDelay.Text = string.Format(ScreenManagerLang.lblDelay_Text, formattedDelay, delayFrames);
        }
        public void UpdateDelayMaxAge(double delay)
        {
            // If the delayer was not allocated, fake a number so that we have a slider stuck at the 0th image.
            sldrDelay.Maximum = delay == 0 ? 0.9 : delay;
        }
        public void UpdateNextImageFilename(string filename)
        {
            fnbImage.Filename = filename;
            fnbImage.Editable = true;
        }
        public void UpdateNextVideoFilename(string filename)
        {
            fnbVideo.Filename = filename;
            fnbVideo.Editable = true;
        }
        public void Toast(string message, int duration)
        {
            
        }
        public void ShowThumbnails()
        {
            if(!pnlCapturedVideos.Visible)
                ToggleCapturedVideosPanel();
        }

        /// <summary>
        /// Disposes resources used by the control.
        /// </summary>
        /// <param name="disposing">true if managed resources should be disposed; otherwise, false.</param>
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (components != null)
                    components.Dispose();

                capturedFilesView.Dispose();
            }

            base.Dispose(disposing);
        }
        #endregion
        
        #endregion
        
        #region Event handlers
        private void BtnCapturedVideosFold_Click(object sender, EventArgs e)
        {
            ToggleCapturedVideosPanel();
        }
        private void BtnClose_Click(object sender, EventArgs e)
        {
            presenter.View_Close();
        }
        private void SldrDelay_ValueChanged(object sender, EventArgs e)
        {
            presenter.View_DelayChanged(sldrDelay.Value);
        }
        private void BtnSettingsClick(object sender, EventArgs e)
        {
            presenter.View_Configure();
        }
        private void BtnGrabClick(object sender, EventArgs e)
        {
            presenter.View_ToggleGrabbing();
        }
        private void LblCameraInfoClick(object sender, EventArgs e)
        {
            presenter.View_Configure();
        }
        private void FNBImage_ImageClick(object sender, EventArgs e)
        {
            presenter.View_EditPathConfiguration(false);
        }
        private void FNBVideo_ImageClick(object sender, EventArgs e)
        {
            presenter.View_EditPathConfiguration(true);
        }
        private void FnbImage_FilenameChanged(object sender, EventArgs e)
        {
            presenter.View_ValidateFilename(fnbImage.Filename);
        }
        private void FnbVideo_FilenameChanged(object sender, EventArgs e)
        {
            presenter.View_ValidateFilename(fnbVideo.Filename);
        }
        private void BtnSnapshot_Click(object sender, EventArgs e)
        {
            presenter.View_SnapshotAsked();
        }
        private void BtnRecordClick(object sender, EventArgs e)
        {
            presenter.View_ToggleRecording();
        }
        private void btnConfigureComposite_Click(object sender, EventArgs e)
        {
            presenter.View_ConfigureComposite();
        }
        #endregion
        
        #region Private methods (pure Form logic)
        private void ToggleCapturedVideosPanel()
        {
            pnlCapturedVideos.Visible = !pnlCapturedVideos.Visible;
            AfterCapturedVideosChange();
        }
        private void AfterCapturedVideosChange()
        {
            if(pnlCapturedVideos.Visible)
            {
                btnFoldCapturedVideosPanel.BackgroundImage = Properties.Capture.section_fold;
                pnlDrawingToolsBar.Top = pnlCapturedVideos.Top - pnlDrawingToolsBar.Height;
                pnlViewport.Height = pnlCapturedVideos.Top - pnlViewport.Top;
            }
            else
            {
                btnFoldCapturedVideosPanel.BackgroundImage = Properties.Capture.section_unfold;
                pnlDrawingToolsBar.Top = pnlControls.Top - pnlDrawingToolsBar.Height;
                pnlViewport.Height = pnlControls.Top - pnlViewport.Top;
            }
        }
        private void ReloadTooltipsCulture()
        {
            toolTips.SetToolTip(btnSettings, ScreenManagerLang.ToolTip_ConfigureCamera);
            toolTips.SetToolTip(btnConfigureComposite, ScreenManagerLang.ToolTip_ConfigureCaptureMosaic);
            toolTips.SetToolTip(btnSnapshot, ScreenManagerLang.Generic_SaveImage);

            if (recording)
                toolTips.SetToolTip(btnRecord, ScreenManagerLang.ToolTip_StopRecording);
            else
                toolTips.SetToolTip(btnRecord, ScreenManagerLang.ToolTip_StartRecording);

            if (grabbing)
                toolTips.SetToolTip(btnGrab, ScreenManagerLang.ToolTip_PauseCamera);
            else
                toolTips.SetToolTip(btnGrab, ScreenManagerLang.ToolTip_StartCamera);
        }
        #endregion

        #region Commands
        protected override bool ExecuteCommand(int cmd)
        {
            if (fnbImage.Focused || fnbVideo.Focused)
                return false;

            if (capturedFilesView.Editing)
                return false;

            if (!presenter.Synched)
                return ExecuteScreenCommand(cmd);

            // If we are in dual capture mode, check if the command is handled by the common controls.
            HotkeyCommand command = Hotkeys.FirstOrDefault(h => h != null && h.CommandCode == cmd);
            if (command == null)
                return false;

            bool dualCaptureHandled = HotkeySettingsManager.IsHandler("DualCapture", command.KeyData);

            if (dualCaptureHandled && DualCommandReceived != null)
            {
                DualCommandReceived(this, new EventArgs<HotkeyCommand>(command));
                return true;
            }
            else
            {
                return ExecuteScreenCommand(cmd);
            }
        }

        private bool ExecuteScreenCommand(int cmd)
        {
            CaptureScreenCommands command = (CaptureScreenCommands)cmd;

            switch (command)
            {
                case CaptureScreenCommands.ToggleGrabbing:
                    presenter.View_ToggleGrabbing();
                    break;
                case CaptureScreenCommands.ToggleRecording:
                    presenter.View_ToggleRecording();
                    break;
                case CaptureScreenCommands.TakeSnapshot:
                    presenter.View_SnapshotAsked();
                    break;
                case CaptureScreenCommands.ResetViewport:
                    presenter.View_DeselectTool();
                    break;
                case CaptureScreenCommands.IncreaseZoom:
                    // Not supported currently, will need to be a command at viewport level.
                    break;
                case CaptureScreenCommands.DecreaseZoom:
                    // Not supported currently, will need to be a command at viewport level.
                    break;
                case CaptureScreenCommands.ResetZoom:
                    // Not supported currently, will need to be a command at viewport level.
                    break;
                case CaptureScreenCommands.OpenConfiguration:
                    presenter.View_Configure();
                    break;
                case CaptureScreenCommands.IncreaseDelay:
                    sldrDelay.Value = sldrDelay.Value + 1;
                    sldrDelay.Invalidate();
                    break;
                case CaptureScreenCommands.DecreaseDelay:
                    sldrDelay.Value = sldrDelay.Value - 1;
                    sldrDelay.Invalidate();
                    break;
                case CaptureScreenCommands.Close:
                    presenter.View_Close();
                    break;
                default:
                    return base.ExecuteCommand(cmd);
            }

            return true;
        }

        #endregion

        
    }
}
