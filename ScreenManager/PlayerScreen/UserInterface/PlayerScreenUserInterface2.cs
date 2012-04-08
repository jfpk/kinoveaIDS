#region License
/*
Copyright � Joan Charmant 2008-2009.
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

#region Using directives
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Resources;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using System.Xml;

using Kinovea.Base;
using Kinovea.ScreenManager.Languages;
using Kinovea.ScreenManager.Properties;
using Kinovea.Services;
using Kinovea.Video;

#endregion

namespace Kinovea.ScreenManager
{
	public partial class PlayerScreenUserInterface : UserControl
	{
		#region Imports Win32
		[DllImport("winmm.dll", SetLastError = true)]
		private static extern uint timeSetEvent(int msDelay, int msResolution, TimerEventHandler handler, ref int userCtx, int eventType);

		[DllImport("winmm.dll", SetLastError = true)]
		private static extern uint timeKillEvent(uint timerEventId);

		private const int TIME_PERIODIC         = 0x01;
		private const int TIME_KILL_SYNCHRONOUS = 0x0100;
        #endregion

		#region Internal delegates for async methods
		private delegate void TimerEventHandler(uint id, uint msg, ref int userCtx, int rsv1, int rsv2);
        private TimerEventHandler m_TimerEventHandler;
		#endregion

		#region Enums
		private enum PlayingMode
		{
			Once,
			Loop,
			Bounce
		}
		#endregion

		#region Properties
		public bool IsCurrentlyPlaying {
			get { return m_bIsCurrentlyPlaying; }
		}
		public bool InteractiveFiltering {
		    get { 
		        return m_InteractiveEffect != null && 
		               m_InteractiveEffect.Draw != null && 
		               m_FrameServer.VideoReader.DecodingMode == VideoDecodingMode.Caching; 
		    }
		}
		public double FrameInterval {
			get {
				return (m_FrameServer.VideoReader.Info.FrameIntervalMilliseconds / (m_fSlowmotionPercentage / 100));
			}
		}
		public double RealtimePercentage
		{
			// RealtimePercentage expresses the speed percentage relative to real time action.
			// It takes high speed camera into account.
			get 
			{ 
				return m_fSlowmotionPercentage / m_fHighSpeedFactor;
			}
			set
			{
				// This happens only in the context of synching 
				// when the other video changed its speed percentage (user or forced).
                // We must NOT trigger the event here, or it will impact the other screen in an infinite loop.
				// Compute back the slow motion percentage relative to the playback framerate.
				double fPlaybackPercentage = value * m_fHighSpeedFactor;
				if(fPlaybackPercentage > 200) fPlaybackPercentage = 200;
				sldrSpeed.Value = (int)fPlaybackPercentage;
				
				// If the other screen is in high speed context, we honor the decimal value.
				// (When it will be changed from this screen's slider, it will be an integer value).
				m_fSlowmotionPercentage = fPlaybackPercentage > 0 ? fPlaybackPercentage : 1;
				
				// Reset timer with new value.
				if (m_bIsCurrentlyPlaying)
				{
					StopMultimediaTimer();
					StartMultimediaTimer((int)GetPlaybackFrameInterval());
				}

				UpdateSpeedLabel();
			}
		}
		public bool Synched
		{
			//get { return m_bSynched; }
			set
			{
				m_bSynched = value;
				
				if(!m_bSynched)
				{
					m_iSyncPosition = 0;
					trkFrame.UpdateSyncPointMarker(m_iSyncPosition);
					trkFrame.Invalidate();
					UpdateCurrentPositionLabel();
					
					m_bSyncMerge = false;
					if(m_SyncMergeImage != null)
						m_SyncMergeImage.Dispose();
				}
				
				buttonPlayingMode.Enabled = !m_bSynched;
			}
		}
		public long SelectionDuration
		{
			// The duration of the selection in ts.
			get { return m_iSelDuration; }	
		}
		public long SyncPosition
		{
			// The absolute ts of the sync point for this video.
			get { return m_iSyncPosition; }
			set
			{
				m_iSyncPosition = value;
				trkFrame.UpdateSyncPointMarker(m_iSyncPosition);
				trkFrame.Invalidate();
				UpdateCurrentPositionLabel();
			}
		}
		public long SyncCurrentPosition
		{
			// The current ts, relative to the selection.
			get { return m_iCurrentPosition - m_iSelStart; }
		}
		public bool SyncMerge
		{
			// Idicates whether we should draw the other screen image on top of this one.
			get { return m_bSyncMerge; }
			set
			{
				m_bSyncMerge = value;
				
				m_FrameServer.CoordinateSystem.FreeMove = m_bSyncMerge;
				
				if(!m_bSyncMerge && m_SyncMergeImage != null)
				{
					m_SyncMergeImage.Dispose();
				}
				
				DoInvalidate();
			}
		}
		public bool DualSaveInProgress
        {
        	set { m_DualSaveInProgress = value; }
        }
		#endregion

		#region Members
		private IPlayerScreenUIHandler m_PlayerScreenUIHandler;
		private FrameServerPlayer m_FrameServer;
		
		// General
		private PreferencesManager m_PrefManager = PreferencesManager.Instance();
		
		// Playback current state
		private bool m_bIsCurrentlyPlaying;
		private int m_iFramesToDecode = 1;
		private uint m_IdMultimediaTimer;
		private PlayingMode m_ePlayingMode = PlayingMode.Loop;
		private double m_fSlowmotionPercentage = 100.0f;	// Always between 1 and 200 : this specific value is not impacted by high speed cameras.
		private bool m_bIsIdle = true;
		private bool m_bIsBusyRendering;
		private int m_RenderingDrops;
		private object m_TimingSync = new object();
		
		// Synchronisation
		private bool m_bSynched;
		private long m_iSyncPosition;
		private bool m_bSyncMerge;
		private Bitmap m_SyncMergeImage;
		private ColorMatrix m_SyncMergeMatrix = new ColorMatrix();
		private ImageAttributes m_SyncMergeImgAttr = new ImageAttributes();
		private float m_SyncAlpha = 0.5f;
		private bool m_DualSaveInProgress;
		
		// Image
		private ViewportManipulator m_viewportManipulator = new ViewportManipulator();
		private bool m_fill;
		private double m_lastUserStretch = 1.0f;
		private bool m_bShowImageBorder;
		private bool m_bManualSqueeze = true; // If it's allowed to manually reduce the rendering surface under the aspect ratio size.
		private static readonly Pen m_PenImageBorder = Pens.SteelBlue;
		private static readonly Size m_MinimalSize = new Size(160,120);
		private bool m_bEnableCustomDecodingSize = true;
		
		// Selection (All values in TimeStamps)
		// trkSelection.minimum and maximum are also in absolute timestamps.
		private long m_iTotalDuration = 100;
		private long m_iSelStart;          	// Valeur absolue, par d�faut �gale � m_iStartingPosition. (pas 0)
		private long m_iSelEnd = 99;          // Value absolue
		private long m_iSelDuration = 100;
		private long m_iCurrentPosition;    	// Valeur absolue dans l'ensemble des timestamps.
		private long m_iStartingPosition;   	// Valeur absolue correspond au timestamp de la premi�re frame.
		private bool m_bHandlersLocked;
		
		// Keyframes, Drawings, etc.
		private int m_iActiveKeyFrameIndex = -1;	// The index of the keyframe we are on, or -1 if not a KF.
		private AbstractDrawingTool m_ActiveTool;
		private DrawingToolPointer m_PointerTool;
		
		private formKeyframeComments m_KeyframeCommentsHub;
		private bool m_bDocked = true;
		private bool m_bTextEdit;
		private Point m_DescaledMouse;    // The current mouse point expressed in the original image size coordinates.

		// Others
		private InteractiveEffect m_InteractiveEffect;
		private const float m_MaxZoomFactor = 6.0F;
		private const int m_MaxRenderingDrops = 6;
		private const int m_MaxDecodingDrops = 6;
		private Double m_fHighSpeedFactor = 1.0f;           	// When capture fps is different from Playing fps.
		private System.Windows.Forms.Timer m_DeselectionTimer = new System.Windows.Forms.Timer();
		private MessageToaster m_MessageToaster;
		private bool m_Constructed;
		
		#region Context Menus
		private ContextMenuStrip popMenu = new ContextMenuStrip();
		private ToolStripMenuItem mnuDirectTrack = new ToolStripMenuItem();
		private ToolStripMenuItem mnuPlayPause = new ToolStripMenuItem();
		private ToolStripMenuItem mnuSetCaptureSpeed = new ToolStripMenuItem();
		private ToolStripMenuItem mnuSavePic = new ToolStripMenuItem();
		private ToolStripMenuItem mnuSendPic = new ToolStripMenuItem();
		private ToolStripMenuItem mnuCloseScreen = new ToolStripMenuItem();

		private ContextMenuStrip popMenuDrawings = new ContextMenuStrip();
		private ToolStripMenuItem mnuConfigureDrawing = new ToolStripMenuItem();
		private ToolStripMenuItem mnuConfigureFading = new ToolStripMenuItem();
		private ToolStripMenuItem mnuConfigureOpacity = new ToolStripMenuItem();
		private ToolStripMenuItem mnuTrackTrajectory = new ToolStripMenuItem();
		private ToolStripMenuItem mnuGotoKeyframe = new ToolStripMenuItem();
		private ToolStripSeparator mnuSepDrawing = new ToolStripSeparator();
		private ToolStripSeparator mnuSepDrawing2 = new ToolStripSeparator();
		private ToolStripMenuItem mnuDeleteDrawing = new ToolStripMenuItem();
		
		private ContextMenuStrip popMenuTrack = new ContextMenuStrip();
		private ToolStripMenuItem mnuRestartTracking = new ToolStripMenuItem();
		private ToolStripMenuItem mnuStopTracking = new ToolStripMenuItem();
		private ToolStripMenuItem mnuDeleteTrajectory = new ToolStripMenuItem();
		private ToolStripMenuItem mnuDeleteEndOfTrajectory = new ToolStripMenuItem();
		private ToolStripMenuItem mnuConfigureTrajectory = new ToolStripMenuItem();
		
		private ContextMenuStrip popMenuChrono = new ContextMenuStrip();
		private ToolStripMenuItem mnuChronoStart = new ToolStripMenuItem();
		private ToolStripMenuItem mnuChronoStop = new ToolStripMenuItem();
		private ToolStripMenuItem mnuChronoHide = new ToolStripMenuItem();
		private ToolStripMenuItem mnuChronoCountdown = new ToolStripMenuItem();
		private ToolStripMenuItem mnuChronoDelete = new ToolStripMenuItem();
		private ToolStripMenuItem mnuChronoConfigure = new ToolStripMenuItem();
		
		private ContextMenuStrip popMenuMagnifier = new ContextMenuStrip();
		private List<ToolStripMenuItem> maginificationMenus = new List<ToolStripMenuItem>();
		private ToolStripMenuItem mnuMagnifierDirect = new ToolStripMenuItem();
		private ToolStripMenuItem mnuMagnifierQuit = new ToolStripMenuItem();
		
		private ContextMenuStrip popMenuMultiDrawing = new ContextMenuStrip();
		private ToolStripMenuItem mnuDeleteMultiDrawingItem = new ToolStripMenuItem();
		#endregion

		ToolStripButton m_btnAddKeyFrame;
		ToolStripButton m_btnShowComments;
		ToolStripButton m_btnToolPresets;
		
		private DropWatcher m_DropWatcher = new DropWatcher();
		private TimeWatcher m_TimeWatcher = new TimeWatcher();
		private LoopWatcher m_LoopWatcher = new LoopWatcher();
		private static readonly log4net.ILog log = log4net.LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);
		#endregion

		#region Constructor
		public PlayerScreenUserInterface(FrameServerPlayer _FrameServer, IPlayerScreenUIHandler _PlayerScreenUIHandler)
		{
			log.Debug("Constructing the PlayerScreen user interface.");
			
			m_PlayerScreenUIHandler = _PlayerScreenUIHandler;
			m_FrameServer = _FrameServer;
			m_FrameServer.Metadata = new Metadata(TimeStampsToTimecode, OnShowClosestFrame);
			
			InitializeComponent();
			BuildContextMenus();
			InitializeDrawingTools();
			AfterSyncAlphaChange();
			m_MessageToaster = new MessageToaster(pbSurfaceScreen);
			
			CommandLineArgumentManager clam = CommandLineArgumentManager.Instance();
			if(!clam.SpeedConsumed)
			{
				sldrSpeed.Value = clam.SpeedPercentage;
				clam.SpeedConsumed = true;
			}
			
			// Most members and controls should be initialized with the right value.
			// So we don't need to do an extra ResetData here.
			
			// Controls that renders differently between run time and design time.
			this.Dock = DockStyle.Fill;
			ShowHideRenderingSurface(false);
			SetupPrimarySelectionPanel();
			SetupKeyframeCommentsHub();
			pnlThumbnails.Controls.Clear();
			DockKeyframePanel(true);

			m_TimerEventHandler = new TimerEventHandler(MultimediaTimer_Tick);
			m_DeselectionTimer.Interval = 3000;
			m_DeselectionTimer.Tick += DeselectionTimer_OnTick;

			EnableDisableActions(false);
		}
		
		#endregion
		
		#region Public Methods
		public void ResetToEmptyState()
		{
			// Called when we load a new video over an already loaded screen.
			// also recalled if the video loaded but the first frame cannot be displayed.

			log.Debug("Reset screen to empty state.");
			
			// 1. Reset all data.
			m_FrameServer.Unload();
			ResetData();
			
			// 2. Reset all interface.
			ShowHideRenderingSurface(false);
			SetupPrimarySelectionPanel();
			pnlThumbnails.Controls.Clear();
			DockKeyframePanel(true);
			UpdateFramesMarkers();
			trkFrame.UpdateSyncPointMarker(m_iSyncPosition);
			trkFrame.Invalidate();
			EnableDisableAllPlayingControls(true);
			EnableDisableDrawingTools(true);
			EnableDisableSnapshot(true);
			buttonPlay.Image = Resources.liqplay17;
			sldrSpeed.Value = 100;
			sldrSpeed.Enabled = false;
			lblFileName.Text = "";
			m_KeyframeCommentsHub.Hide();
			UpdatePlayingModeButton();
			
			m_PlayerScreenUIHandler.PlayerScreenUI_Reset();
		}
		public void EnableDisableActions(bool _bEnable)
		{
			// Called back after a load error.
			// Prevent any actions.
			if(!_bEnable)
				DisablePlayAndDraw();
			
			EnableDisableSnapshot(_bEnable);
			EnableDisableDrawingTools(_bEnable);
			
			if(_bEnable && m_FrameServer.Loaded && m_FrameServer.VideoReader.IsSingleFrame)
				EnableDisableAllPlayingControls(false);
			else
				EnableDisableAllPlayingControls(_bEnable);				
		}
		public int PostLoadProcess()
		{
			//---------------------------------------------------------------------------
			// Configure the interface according to he video and try to read first frame.
			// Called from CommandLoadMovie when VideoFile.Load() is successful.
			//---------------------------------------------------------------------------
			
			// By default the filename of metadata will be the one of the video.
			m_FrameServer.Metadata.FullPath = m_FrameServer.VideoReader.FilePath;
			
			DemuxMetadata();
			ShowNextFrame(-1, true);
			UpdatePositionUI();

			if (m_FrameServer.VideoReader.Current == null)
			{
				m_FrameServer.Unload();
				log.Error("First frame couldn't be loaded - aborting");
				return -1;
			}
			else if(m_iCurrentPosition < 0)
			{
			    // First frame loaded but inconsistency. (Seen with some AVCHD)
			    m_FrameServer.Unload();
				log.Error(String.Format("First frame loaded but negative timestamp ({0}) - aborting", m_iCurrentPosition));
				return -2;
			}
			
			
			//---------------------------------------------------------------------------------------
			// First frame loaded.
			//
			// We will now update the internal data of the screen ui and
			// set up the various child controls (like the timelines).
			// Call order matters.
			// Some bugs come from variations between what the file infos advertised and the reality.
			// We fix what we can with the help of data read from the first frame or 
			// from the analysis mode switch if successful.
			//---------------------------------------------------------------------------------------
			
			DoInvalidate();

			m_iStartingPosition = m_iCurrentPosition;
			m_iTotalDuration = m_FrameServer.VideoReader.Info.DurationTimeStamps;
			m_iSelStart = m_iStartingPosition;
			m_iSelEnd = m_FrameServer.VideoReader.WorkingZone.End;
			m_iSelDuration  = m_iTotalDuration;
			
			if(!m_FrameServer.VideoReader.CanChangeWorkingZone)
			    EnableDisableWorkingZoneControls(false);

			//m_iCurrentPosition = m_iSelStart;
			// FIXME: This should be the responsibility of the reader.
			//m_FrameServer.VideoReader.Infos.iFirstTimeStamp = m_iCurrentPosition;
			//m_iStartingPosition = m_iCurrentPosition;
			//m_iTotalDuration = m_iSelDuration;
			
			// Update the control.
			// FIXME - already done in ImportSelectionToMemory ?
			SetupPrimarySelectionPanel();
			
			// Other various infos.
			m_FrameServer.SetupMetadata();
			m_PointerTool.SetImageSize(m_FrameServer.VideoReader.Info.AspectRatioSize);
			m_viewportManipulator.Initialize(m_FrameServer.VideoReader);
			
			UpdateFilenameLabel();
			sldrSpeed.Enabled = true;

			// Screen position and size.
			m_FrameServer.CoordinateSystem.SetOriginalSize(m_FrameServer.VideoReader.Info.AspectRatioSize);
			m_FrameServer.CoordinateSystem.ReinitZoom();
			SetUpForNewMovie();
			m_KeyframeCommentsHub.UserActivated = false;

			if (!m_FrameServer.Metadata.HasData)
				LookForLinkedAnalysis();
			
			// Check for startup kva
			string folder = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData) + "\\Kinovea\\";
            string startupFile = folder + "\\playback.kva";
            if(File.Exists(startupFile))
                m_FrameServer.Metadata.Load(startupFile, true);
			
			if (m_FrameServer.Metadata.HasData)
			{
			    PostImportMetadata();
			    m_FrameServer.Metadata.CleanupHash();
			}
			else
			{
                m_FrameServer.AutoSaver.Clean();
                m_FrameServer.AutoSaver.Start();
			}
			
			Application.Idle += PostLoad_Idle;
			
			return 0;
		}
		public void PostImportMetadata()
		{
			//----------------------------------------------------------
			// Analysis file or stream was imported into metadata.
			// Now we need to load each frames and do some scaling.
			//
			// Public because accessed from :
			// 	ScreenManager upon loading standalone analysis.
			//----------------------------------------------------------

			int iOutOfRange = -1;
			int iCurrentKeyframe = -1;

			foreach (Keyframe kf in m_FrameServer.Metadata.Keyframes)
			{
				iCurrentKeyframe++;

				if (kf.Position < (m_FrameServer.VideoReader.Info.FirstTimeStamp + m_FrameServer.VideoReader.Info.DurationTimeStamps))
				{
					// Goto frame.
					m_iFramesToDecode = 1;
					ShowNextFrame(kf.Position, true);
					
					if(m_FrameServer.CurrentImage == null)
					    continue;
					
					UpdatePositionUI();

					// Readjust and complete the Keyframe
					kf.Position = m_iCurrentPosition;
					kf.ImportImage(m_FrameServer.CurrentImage);
					kf.GenerateDisabledThumbnail();

					// EditBoxes
					foreach (AbstractDrawing ad in kf.Drawings)
					{
						if (ad is DrawingText)
						{
							((DrawingText)ad).ContainerScreen = pbSurfaceScreen;
							panelCenter.Controls.Add(((DrawingText)ad).EditBox);
							((DrawingText)ad).EditBox.BringToFront();
						}
					}
				}
				else
				{
					// TODO - Alert box to inform that some images couldn't be matched.
					if (iOutOfRange < 0)
					{
						iOutOfRange = iCurrentKeyframe;
					}
				}
			}

			if (iOutOfRange != -1)
			{
				// Some keyframes were out of range. remove them.
				m_FrameServer.Metadata.Keyframes.RemoveRange(iOutOfRange, m_FrameServer.Metadata.Keyframes.Count - iOutOfRange);
			}
			
            UpdateFilenameLabel();
			OrganizeKeyframes();
			if(m_FrameServer.Metadata.Count > 0)
			{
				DockKeyframePanel(false);
			}
			
			m_iFramesToDecode = 1;
			ShowNextFrame(m_iSelStart, true);
			UpdatePositionUI();
			ActivateKeyframe(m_iCurrentPosition);

			m_FrameServer.SetupMetadata();
			m_PointerTool.SetImageSize(m_FrameServer.Metadata.ImageSize);

			m_FrameServer.AutoSaver.Clean();
			m_FrameServer.AutoSaver.Start();
			
			DoInvalidate();
		}
        public void UpdateWorkingZone(bool _bForceReload)
        {
            if (!m_FrameServer.Loaded)
                return;

            if(m_FrameServer.VideoReader.CanChangeWorkingZone)
            {
                StopPlaying();
                m_PlayerScreenUIHandler.PlayerScreenUI_PauseAsked();
                VideoSection newZone = new VideoSection(m_iSelStart, m_iSelEnd);
                m_FrameServer.VideoReader.UpdateWorkingZone(newZone, _bForceReload, m_PrefManager.WorkingZoneSeconds, m_PrefManager.WorkingZoneMemory, ProgressWorker);
                //log.DebugFormat("After updating working zone. Asked:{0}, got: {1}", newZone, m_FrameServer.VideoReader.WorkingZone);
                
                ResizeUpdate(true);
            }
            
            // Reupdate back the locals as the reader uses more precise values.
            m_iSelStart = m_FrameServer.VideoReader.WorkingZone.Start;
            m_iSelEnd = m_FrameServer.VideoReader.WorkingZone.End;
            m_iSelDuration = m_iSelEnd - m_iSelStart + m_FrameServer.VideoReader.Info.AverageTimeStampsPerFrame;
            
            if(trkSelection.SelStart != m_iSelStart)
                trkSelection.SelStart = m_iSelStart;

            if(trkSelection.SelEnd != m_iSelEnd)
                trkSelection.SelEnd = m_iSelEnd;
                    
            trkFrame.Remap(m_iSelStart, m_iSelEnd);
            trkFrame.ReportOnMouseMove = m_FrameServer.VideoReader.DecodingMode == VideoDecodingMode.Caching;
            
            m_iFramesToDecode = 1;
            ShowNextFrame(m_iSelStart, true);
            
            UpdatePositionUI();
            UpdateSelectionLabels();
            OnPoke();
            m_PlayerScreenUIHandler.PlayerScreenUI_SelectionChanged(true);
		}
        private void ProgressWorker(DoWorkEventHandler _doWork)
        {
            formProgressBar2 fpb = new formProgressBar2(true, false, _doWork);
            fpb.ShowDialog();
            fpb.Dispose();
        }
        public void DisplayAsActiveScreen(bool _bActive)
		{
			// Called from ScreenManager.
			ShowBorder(_bActive);
		}
		public void StopPlaying()
		{
			StopPlaying(true);
		}
		public void SyncSetCurrentFrame(long _iFrame, bool _bAllowUIUpdate)
		{
			// Called during static sync.
			// Common position changed, we get a new frame to jump to.
			// target frame may be over the total.

			if (m_FrameServer.Loaded)
			{
				m_iFramesToDecode = 1;
                StopPlaying();
				
				if (_iFrame == -1)
				{
					// Special case for +1 frame.
					if (m_iCurrentPosition < m_iSelEnd)
					{
						ShowNextFrame(-1, _bAllowUIUpdate);
					}
				}
				else
				{
					m_iCurrentPosition = _iFrame * m_FrameServer.VideoReader.Info.AverageTimeStampsPerFrame;
					m_iCurrentPosition += m_iSelStart;
					
					if (m_iCurrentPosition > m_iSelEnd) m_iCurrentPosition = m_iSelEnd;
					
					ShowNextFrame(m_iCurrentPosition, _bAllowUIUpdate);
				}

				if(_bAllowUIUpdate)
				{
					UpdatePositionUI();
					ActivateKeyframe(m_iCurrentPosition);
				}
			}
		}
		public void RefreshImage()
		{
			// For cases where surfaceScreen.Invalidate() is not enough.
			// Not needed if we are playing.
			if (m_FrameServer.Loaded && !m_bIsCurrentlyPlaying)
				ShowNextFrame(m_iCurrentPosition, true);
		}
		public void RefreshUICulture()
		{
			// Labels
			lblSelStartSelection.AutoSize = true;
			lblSelDuration.AutoSize = true;

			lblWorkingZone.Text = ScreenManagerLang.lblWorkingZone_Text;
			UpdateSpeedLabel();
			UpdateSelectionLabels();
			UpdateCurrentPositionLabel();
			
			RepositionSpeedControl();			
			ReloadTooltipsCulture();
			ReloadMenusCulture();
			m_KeyframeCommentsHub.RefreshUICulture();

			// Because this method is called when we change the general preferences,
			// we can use it to update data too.
			
			// Keyframes positions.
			if (m_FrameServer.Metadata.Count > 0)
			{
				EnableDisableKeyframes();
			}
			
			m_FrameServer.Metadata.CalibrationHelper.CurrentSpeedUnit = m_PrefManager.SpeedUnit;
			m_FrameServer.Metadata.UpdateTrajectoriesForKeyframes();

			// Refresh image to update timecode in chronos, grids colors, default fading, etc.
			DoInvalidate();
		}
		public void SetInteractiveEffect(InteractiveEffect _effect)
		{
		    if(_effect == null)
		        return;
		    
		    m_InteractiveEffect = _effect;
		    
		    DisablePlayAndDraw();
		    EnableDisableAllPlayingControls(false);
			EnableDisableDrawingTools(false);
			DockKeyframePanel(true);
			m_fill = true;
			ResizeUpdate(true);
		}
		public void DeactivateInteractiveEffect()
		{
		    m_InteractiveEffect = null;
			EnableDisableAllPlayingControls(true);
			EnableDisableDrawingTools(true);
			DoInvalidate();
		}
		public void SetSyncMergeImage(Bitmap _SyncMergeImage, bool _bUpdateUI)
		{
			m_SyncMergeImage = _SyncMergeImage;
				
			if(_bUpdateUI)
			{
				// Ask for a repaint. We don't wait for the next frame to be drawn
				// because the user may be manually moving the other video.
				DoInvalidate();
			}
		}
		public bool OnKeyPress(Keys _keycode)
		{
		    if (!m_FrameServer.Loaded)
		        return false;
			
		    bool bWasHandled = false;
			
			// Note: All keystrokes handled here must first be registered in ScreenManager's PrefilterMessage.
			switch (_keycode)
			{
				case Keys.Space:
				case Keys.Return:
					{
						OnButtonPlay();
						bWasHandled = true;
						break;
					}
				case Keys.Escape:
					{
						DisablePlayAndDraw();
						DoInvalidate();
						bWasHandled = true;
						break;
					}
				case Keys.Left:
					{
						if ((ModifierKeys & Keys.Control) == Keys.Control)
						{
							// Previous keyframe
							GotoPreviousKeyframe();
						}
						else
						{
							if (((ModifierKeys & Keys.Shift) == Keys.Shift) && m_iCurrentPosition <= m_iSelStart)
							{
								// Shift + Left on first = loop backward.
								buttonGotoLast_Click(null, EventArgs.Empty);
							}
							else
							{
								// Previous frame
								buttonGotoPrevious_Click(null, EventArgs.Empty);
							}
						}
						bWasHandled = true;
						break;
					}
				case Keys.Right:
					{
						if ((ModifierKeys & Keys.Control) == Keys.Control)
						{
							// Next keyframe
							GotoNextKeyframe();
						}
						else
						{
							// Next frame
							buttonGotoNext_Click(null, EventArgs.Empty);
						}
						bWasHandled = true;
						break;
					}
				case Keys.Add:
					{
			            if((ModifierKeys & Keys.Alt) == Keys.Alt)
			                IncreaseSyncAlpha();
			            else if ((ModifierKeys & Keys.Control) == Keys.Control)
                            IncreaseDirectZoom();
						
			            bWasHandled = true;
						break;
					}
				case Keys.Subtract:
					{
						if((ModifierKeys & Keys.Alt) == Keys.Alt)
			                DecreaseSyncAlpha();
			            else if ((ModifierKeys & Keys.Control) == Keys.Control)
                            DecreaseDirectZoom();
						
			            bWasHandled = true;
						break;
					}
				case Keys.F6:
					{
						AddKeyframe();
						bWasHandled = true;
						break;
					}
				case Keys.F7:
					{
						// Unused.
						break;
					}
				case Keys.Delete:
					{
						if ((ModifierKeys & Keys.Control) == Keys.Control)
						{
							// Remove Keyframe
							if (m_iActiveKeyFrameIndex >= 0)
							{
								RemoveKeyframe(m_iActiveKeyFrameIndex);
							}
						}
						else
						{
							// Remove selected Drawing
							// Note: Should only work if the Drawing is currently being moved...
							DeleteSelectedDrawing();
						}
						bWasHandled = true;
						break;
					}
				case Keys.End:
					{
						buttonGotoLast_Click(null, EventArgs.Empty);
						bWasHandled = true;
						break;
					}
				case Keys.Home:
					{
						buttonGotoFirst_Click(null, EventArgs.Empty);
						bWasHandled = true;
						break;
					}
				case Keys.Down:
				case Keys.Up:
					{
						sldrSpeed_KeyDown(null, new KeyEventArgs(_keycode));
						bWasHandled = true;
						break;
					}
			    case Keys.NumPad0:
                    {
			            if ((ModifierKeys & Keys.Control) == Keys.Control)
			            {
			                UnzoomDirectZoom(true);
			                bWasHandled = true;
			            }
			            break;
			        }
				default:
					break;
			}

			return bWasHandled;
		}
		public void AspectRatioChanged()
		{
			m_FrameServer.Metadata.ImageSize = m_FrameServer.VideoReader.Info.AspectRatioSize;
			m_PointerTool.SetImageSize(m_FrameServer.VideoReader.Info.AspectRatioSize);
			m_FrameServer.CoordinateSystem.SetOriginalSize(m_FrameServer.VideoReader.Info.AspectRatioSize);
			m_FrameServer.CoordinateSystem.ReinitZoom();
			
			ResizeUpdate(true);
		}
		public void FullScreen(bool _bFullScreen)
		{
		    if (_bFullScreen && !m_fill)
			{
				m_fill = true;
				ResizeUpdate(true);
			}
		}
		public void AddImageDrawing(string _filename, bool _bIsSvg)
		{
			// Add an image drawing from a file.
			// Mimick all the actions that are normally taken when we select a drawing tool and click on the image.
			if(!m_FrameServer.Loaded)
                return;

            BeforeAddImageDrawing();
		
			if(File.Exists(_filename))
			{
				try
				{
					if(_bIsSvg)
					{
						DrawingSVG dsvg = new DrawingSVG(m_FrameServer.Metadata.ImageSize.Width,
						                                 m_FrameServer.Metadata.ImageSize.Height, 
						                                 m_iCurrentPosition, 
						                                 m_FrameServer.Metadata.AverageTimeStampsPerFrame, 
						                                 _filename);
					
						m_FrameServer.Metadata[m_iActiveKeyFrameIndex].AddDrawing(dsvg);
					}
					else
					{
						DrawingBitmap dbmp = new DrawingBitmap( m_FrameServer.Metadata.ImageSize.Width,
						                                 		m_FrameServer.Metadata.ImageSize.Height, 
						                                 		m_iCurrentPosition, 
						                                 		m_FrameServer.Metadata.AverageTimeStampsPerFrame, 
						                                 		_filename);
					
						m_FrameServer.Metadata[m_iActiveKeyFrameIndex].AddDrawing(dbmp);	
					}
				}
				catch
				{
					// An error occurred during the creation.
					// example : external DTD an no network or invalid svg file.
					// TODO: inform the user.
				}
			}
			
			AfterAddImageDrawing();
		}
		public void AddImageDrawing(Bitmap _bmp)
		{
			// Add an image drawing from a bitmap.
			// Mimick all the actions that are normally taken when we select a drawing tool and click on the image.
			// TODO: use an actual DrawingTool class for this!?
			if(m_FrameServer.Loaded)
			{
				BeforeAddImageDrawing();
			
				DrawingBitmap dbmp = new DrawingBitmap( m_FrameServer.Metadata.ImageSize.Width,
							                                 		m_FrameServer.Metadata.ImageSize.Height, 
							                                 		m_iCurrentPosition, 
							                                 		m_FrameServer.Metadata.AverageTimeStampsPerFrame, 
							                                 		_bmp);
						
				m_FrameServer.Metadata[m_iActiveKeyFrameIndex].AddDrawing(dbmp);
				
				AfterAddImageDrawing();
			}
		}
		private void BeforeAddImageDrawing()
		{
			if(m_bIsCurrentlyPlaying)
			{
				StopPlaying();
				m_PlayerScreenUIHandler.PlayerScreenUI_PauseAsked();
				ActivateKeyframe(m_iCurrentPosition);	
			}
					
			PrepareKeyframesDock();
			m_FrameServer.Metadata.AllDrawingTextToNormalMode();
			m_FrameServer.Metadata.Deselect();
			
			// Add a KeyFrame here if it doesn't exist.
			AddKeyframe();
			
		}
		private void AfterAddImageDrawing()
		{
		    m_FrameServer.Metadata.Deselect();
			m_ActiveTool = m_PointerTool;
			SetCursor(m_PointerTool.GetCursor(0));
			
			DoInvalidate();
		}
		#endregion
		
		#region Various Inits & Setups
		private void InitializeDrawingTools()
		{
			m_PointerTool = new DrawingToolPointer();
			m_ActiveTool = m_PointerTool;
			
			stripDrawingTools.Left = 3;
			
			// Special button: Add key image
			m_btnAddKeyFrame = CreateToolButton();
        	m_btnAddKeyFrame.Image = Drawings.addkeyimage;
        	m_btnAddKeyFrame.Click += btnAddKeyframe_Click;
        	m_btnAddKeyFrame.ToolTipText = ScreenManagerLang.ToolTip_AddKeyframe;
        	stripDrawingTools.Items.Add(m_btnAddKeyFrame);
        	
        	// Pointer tool button
			AddToolButton(m_PointerTool, drawingTool_Click);
			stripDrawingTools.Items.Add(new ToolStripSeparator());
			
			// Special button: Key image comments
			m_btnShowComments = CreateToolButton();
        	m_btnShowComments.Image = Resources.comments2;
        	m_btnShowComments.Click += btnShowComments_Click;
        	m_btnShowComments.ToolTipText = ScreenManagerLang.ToolTip_ShowComments;
        	stripDrawingTools.Items.Add(m_btnShowComments);

        	// All other tools
        	AddToolButtonWithMenu(new AbstractDrawingTool[]{ToolManager.Label, ToolManager.AutoNumbers}, 0, drawingTool_Click);
			AddToolButton(ToolManager.Pencil, drawingTool_Click);
			AddToolButtonPosture(drawingTool_Click);
			AddToolButtonWithMenu(new AbstractDrawingTool[]{ToolManager.Line, ToolManager.Circle}, 0, drawingTool_Click);
			AddToolButton(ToolManager.Arrow, drawingTool_Click);
			AddToolButton(ToolManager.CrossMark, drawingTool_Click);
			//AddToolButtonWithMenu(new AbstractDrawingTool[]{ToolManager.Angle}, 0, drawingTool_Click);
			AddToolButton(ToolManager.Angle, drawingTool_Click);
			AddToolButton(ToolManager.Chrono, drawingTool_Click);
			AddToolButtonWithMenu(new AbstractDrawingTool[]{ToolManager.Grid, ToolManager.Plane}, 0, drawingTool_Click);
			AddToolButton(ToolManager.Spotlight, drawingTool_Click);
			
			AddToolButton(ToolManager.Magnifier, btnMagnifier_Click);

			// Special button: Tool presets
			m_btnToolPresets = CreateToolButton();
        	m_btnToolPresets.Image = Resources.SwatchIcon3;
        	m_btnToolPresets.Click += btnColorProfile_Click;
        	m_btnToolPresets.ToolTipText = ScreenManagerLang.ToolTip_ColorProfile;
        	stripDrawingTools.Items.Add(m_btnToolPresets);
		}
		private ToolStripButton CreateToolButton()
		{
			ToolStripButton btn = new ToolStripButton();
			btn.AutoSize = false;
        	btn.DisplayStyle = ToolStripItemDisplayStyle.Image;
        	btn.ImageScaling = ToolStripItemImageScaling.None;
        	btn.Size = new Size(25, 25);
        	btn.AutoToolTip = false;
        	return btn;
		}
		private void AddToolButton(AbstractDrawingTool _tool, EventHandler _handler)
		{
			ToolStripButton btn = CreateToolButton();
        	btn.Image = _tool.Icon;
        	btn.Tag = _tool;
        	btn.Click += _handler;
        	btn.ToolTipText = _tool.DisplayName;
        	stripDrawingTools.Items.Add(btn);
		}
		private void AddToolButtonWithMenu(AbstractDrawingTool[] _tools, int selectedIndex, EventHandler _handler)
		{
		    // Adds a button with a sub menu.
		    // Each menu item will act as a button, and the master button will take the icon of the selected menu.
		    
		    ToolStripButtonWithDropDown btn = new ToolStripButtonWithDropDown();
			btn.AutoSize = false;
        	btn.DisplayStyle = ToolStripItemDisplayStyle.Image;
        	btn.ImageScaling = ToolStripItemImageScaling.None;
        	btn.Size = new Size(25, 25);
        	btn.AutoToolTip = false;

        	for(int i = _tools.Length-1;i>=0;i--)
        	{
        	    AbstractDrawingTool tool = _tools[i];
        	    ToolStripMenuItem item = new ToolStripMenuItem();
        	    item.Image = tool.Icon;
        	    item.Text = tool.DisplayName;
        	    item.Tag = tool;
        	    int indexClosure = _tools.Length - 1 - i;
        	    item.Click += (s,e) =>
        	    {
        	        btn.SelectedIndex = indexClosure;
        	        _handler(s,e);
        	    };

        	    btn.DropDownItems.Add(item);
        	}
        	
        	btn.SelectedIndex = _tools.Length - 1 - selectedIndex;
        	
        	stripDrawingTools.Items.Add(btn);
		}
		private void AddToolButtonPosture(EventHandler _handler)
        {
		    if(GenericPostureManager.Tools.Count > 0)
		        AddToolButtonWithMenu(GenericPostureManager.Tools.ToArray(), 0, drawingTool_Click);
		    
            /*string dir = @"C:\Users\Joan\Dev  Prog\Videa\Bitbucket\ToolLaboratory\Tools\postures";
            
            if(!Directory.Exists(dir))
                return;
            
            List<AbstractDrawingTool> tools = new List<AbstractDrawingTool>();

            foreach (string f in Directory.GetFiles(dir))
            {
                if (!Path.GetExtension(f).ToLower().Equals(".xml"))
                    continue;

                DrawingToolGenericPostureSandbox tool = new DrawingToolGenericPostureSandbox();
                tool.SetFile(f);
                tools.Add(tool);
            }
            
            if(tools.Count > 0)
                AddToolButtonWithMenu(tools.ToArray(), 0, drawingTool_Click);*/
		}
		private void ResetData()
		{
			m_iFramesToDecode = 1;
			
			m_fSlowmotionPercentage = 100.0;
			DeactivateInteractiveEffect();
			m_bIsCurrentlyPlaying = false;
			m_ePlayingMode = PlayingMode.Loop;
			m_fill = false;
			m_FrameServer.CoordinateSystem.Reset();
			
			// Sync
			m_bSynched = false;
			m_iSyncPosition = 0;
			m_bSyncMerge = false;
			if(m_SyncMergeImage != null)
				m_SyncMergeImage.Dispose();
			
			m_bShowImageBorder = false;
			
			SetupPrimarySelectionData(); 	// Should not be necessary when every data is coming from m_FrameServer.
			
			m_bHandlersLocked = false;
			
			m_iActiveKeyFrameIndex = -1;
			m_ActiveTool = m_PointerTool;
			
			m_bDocked = true;
			m_bTextEdit = false;
			DrawingToolLine2D.ShowMeasure = false;
			DrawingToolCross2D.ShowCoordinates = false;
			
			m_fHighSpeedFactor = 1.0f;
			UpdateSpeedLabel();
		}
		private void DemuxMetadata()
		{
			// Try to find metadata muxed inside the file and load it.
			string kva = m_FrameServer.VideoReader.ReadMetadata();
			if (!string.IsNullOrEmpty(kva))
			{
			    m_FrameServer.Metadata = new Metadata(kva, m_FrameServer.VideoReader.Info,  TimeStampsToTimecode, OnShowClosestFrame);
                UpdateFramesMarkers();
				OrganizeKeyframes();
			}
		}
		private void SetupPrimarySelectionData()
		{
			// Setup data
			if (m_FrameServer.Loaded)
			{
				m_iSelStart = m_iStartingPosition;
				m_iSelEnd = m_iStartingPosition + m_iTotalDuration - m_FrameServer.VideoReader.Info.AverageTimeStampsPerFrame;
				m_iSelDuration = m_iTotalDuration;
			}
			else
			{
				m_iSelStart = 0;
				m_iSelEnd = 99;
				m_iSelDuration = 100;
				m_iTotalDuration = 100;
				
				m_iCurrentPosition = 0;
				m_iStartingPosition = 0;
			}
		}
		private void SetupPrimarySelectionPanel()
		{
			// Setup controls & labels.
			// Update internal state only, doesn't trigger the events.
			trkSelection.UpdateInternalState(m_iSelStart, m_iSelEnd, m_iSelStart, m_iSelEnd, m_iSelStart);
			UpdateSelectionLabels();
		}
		private void SetUpForNewMovie()
		{
			OnPoke();
		}
		private void SetupKeyframeCommentsHub()
		{
			DelegatesPool dp = DelegatesPool.Instance();
			if (dp.MakeTopMost != null)
			{
				m_KeyframeCommentsHub = new formKeyframeComments(this);
				dp.MakeTopMost(m_KeyframeCommentsHub);
			}
		}
		private void LookForLinkedAnalysis()
		{
			// Look for an Anlaysis with the same file name in the same directory.

			// Complete path of hypothetical Analysis.
			string kvaFile = Path.GetDirectoryName(m_FrameServer.VideoReader.FilePath);
			kvaFile = kvaFile + "\\" + Path.GetFileNameWithoutExtension(m_FrameServer.VideoReader.FilePath) + ".kva";
			
			if (File.Exists(kvaFile))
				m_FrameServer.Metadata.Load(kvaFile, true);
		}
		private void UpdateFilenameLabel()
		{
			lblFileName.Text = Path.GetFileName(m_FrameServer.VideoReader.FilePath);
		}
		private void ShowHideRenderingSurface(bool _bShow)
		{
			ImageResizerNE.Visible = _bShow;
			ImageResizerNW.Visible = _bShow;
			ImageResizerSE.Visible = _bShow;
			ImageResizerSW.Visible = _bShow;
			pbSurfaceScreen.Visible = _bShow;
		}
		private void BuildContextMenus()
		{
			// Attach the event handlers and build the menus.
			
			// 1. Default context menu.
			mnuDirectTrack.Click += new EventHandler(mnuDirectTrack_Click);
			mnuDirectTrack.Image = Properties.Drawings.track;
			mnuPlayPause.Click += new EventHandler(buttonPlay_Click);
			mnuSetCaptureSpeed.Click += new EventHandler(mnuSetCaptureSpeed_Click);
			mnuSetCaptureSpeed.Image = Properties.Resources.camera_speed;
			mnuSavePic.Click += new EventHandler(btnSnapShot_Click);
			mnuSavePic.Image = Properties.Resources.picture_save;
			mnuSendPic.Click += new EventHandler(mnuSendPic_Click);
			mnuSendPic.Image = Properties.Resources.image;
			mnuCloseScreen.Click += new EventHandler(btnClose_Click);
			mnuCloseScreen.Image = Properties.Resources.film_close3;
			popMenu.Items.AddRange(new ToolStripItem[] { mnuDirectTrack, mnuSetCaptureSpeed, mnuSavePic, mnuSendPic, new ToolStripSeparator(), mnuCloseScreen });

			// 2. Drawings context menu (Configure, Delete, Track this)
			mnuConfigureDrawing.Click += new EventHandler(mnuConfigureDrawing_Click);
			mnuConfigureDrawing.Image = Properties.Drawings.configure;
			mnuConfigureFading.Click += new EventHandler(mnuConfigureFading_Click);
			mnuConfigureFading.Image = Properties.Drawings.persistence;
			mnuConfigureOpacity.Click += new EventHandler(mnuConfigureOpacity_Click);
			mnuConfigureOpacity.Image = Properties.Drawings.persistence;
			mnuTrackTrajectory.Click += new EventHandler(mnuTrackTrajectory_Click);
			mnuTrackTrajectory.Image = Properties.Drawings.track;
			mnuGotoKeyframe.Click += new EventHandler(mnuGotoKeyframe_Click);
			mnuGotoKeyframe.Image = Properties.Resources.page_white_go;
			mnuDeleteDrawing.Click += new EventHandler(mnuDeleteDrawing_Click);
			mnuDeleteDrawing.Image = Properties.Drawings.delete;
			
			// 3. Tracking pop menu (Restart, Stop tracking)
			mnuStopTracking.Click += new EventHandler(mnuStopTracking_Click);
			mnuStopTracking.Visible = false;
			mnuStopTracking.Image = Properties.Drawings.trackstop;
			mnuRestartTracking.Click += new EventHandler(mnuRestartTracking_Click);
			mnuRestartTracking.Visible = false;
			mnuRestartTracking.Image = Properties.Drawings.trackingplay;
			mnuDeleteTrajectory.Click += new EventHandler(mnuDeleteTrajectory_Click);
			mnuDeleteTrajectory.Image = Properties.Drawings.delete;
			mnuDeleteEndOfTrajectory.Click += new EventHandler(mnuDeleteEndOfTrajectory_Click);
			//mnuDeleteEndOfTrajectory.Image = Properties.Resources.track_trim2;
			mnuConfigureTrajectory.Click += new EventHandler(mnuConfigureTrajectory_Click);
			mnuConfigureTrajectory.Image = Properties.Drawings.configure;
			popMenuTrack.Items.AddRange(new ToolStripItem[] { mnuConfigureTrajectory, new ToolStripSeparator(), mnuStopTracking, mnuRestartTracking, new ToolStripSeparator(), mnuDeleteEndOfTrajectory, mnuDeleteTrajectory });

			// 4. Chrono pop menu (Start, Stop, Hide, etc.)
			mnuChronoConfigure.Click += new EventHandler(mnuChronoConfigure_Click);
			mnuChronoConfigure.Image = Properties.Drawings.configure;
			mnuChronoStart.Click += new EventHandler(mnuChronoStart_Click);
			mnuChronoStart.Image = Properties.Drawings.chronostart;
			mnuChronoStop.Click += new EventHandler(mnuChronoStop_Click);
			mnuChronoStop.Image = Properties.Drawings.chronostop;
			mnuChronoCountdown.Click += new EventHandler(mnuChronoCountdown_Click);
			mnuChronoCountdown.Checked = false;
			mnuChronoCountdown.Enabled = false;
			mnuChronoHide.Click += new EventHandler(mnuChronoHide_Click);
			mnuChronoHide.Image = Properties.Drawings.hide;
			mnuChronoDelete.Click += new EventHandler(mnuChronoDelete_Click);
			mnuChronoDelete.Image = Properties.Drawings.delete;
			popMenuChrono.Items.AddRange(new ToolStripItem[] { mnuChronoConfigure, new ToolStripSeparator(), mnuChronoStart, mnuChronoStop, mnuChronoCountdown, new ToolStripSeparator(), mnuChronoHide, mnuChronoDelete, });

			// 5. Magnifier
			foreach(double factor in Magnifier.MagnificationFactors)
			    maginificationMenus.Add(CreateMagnificationMenu(factor));
			maginificationMenus[1].Checked = true;
			popMenuMagnifier.Items.AddRange(maginificationMenus.ToArray());
			
			mnuMagnifierDirect.Click += new EventHandler(mnuMagnifierDirect_Click);
			mnuMagnifierDirect.Image = Properties.Resources.arrow_out;
			mnuMagnifierQuit.Click += new EventHandler(mnuMagnifierQuit_Click);
			mnuMagnifierQuit.Image = Properties.Resources.hide;
			popMenuMagnifier.Items.AddRange(new ToolStripItem[] { new ToolStripSeparator(), mnuMagnifierDirect, mnuMagnifierQuit });
			
			// 6. Spotlight.
			mnuDeleteMultiDrawingItem.Image = Properties.Drawings.delete;
			mnuDeleteMultiDrawingItem.Click += mnuDeleteMultiDrawingItem_Click;
			popMenuMultiDrawing.Items.AddRange(new ToolStripItem[] { mnuDeleteMultiDrawingItem});
			
			// The right context menu and its content will be choosen upon MouseDown.
			panelCenter.ContextMenuStrip = popMenu;
			
			// Load texts
			ReloadMenusCulture();
		}
        private ToolStripMenuItem CreateMagnificationMenu(double magnificationFactor)
		{
		    ToolStripMenuItem mnu = new ToolStripMenuItem();
			mnu.Tag = magnificationFactor;
			mnu.Text = String.Format(ScreenManagerLang.mnuMagnification, magnificationFactor.ToString());
			mnu.Click += mnuMagnifierChangeMagnification;
			return mnu;
		}
        private void PostLoad_Idle(object sender, EventArgs e)
        {
            Application.Idle -= PostLoad_Idle;
            m_Constructed = true;

		    if(!m_FrameServer.Loaded)
		        return;
		    
		    log.DebugFormat("Post load event.");
		    
		    // This would be a good time to start the prebuffering if supported.
		    // The UpdateWorkingZone call may try to go full cache if possible.
		    m_FrameServer.VideoReader.PostLoad();
			UpdateWorkingZone(true);
			UpdateFramesMarkers();
			
			ShowHideRenderingSurface(true);
		    
			ResizeUpdate(true);
		}
        #endregion
		
		#region Misc Events
		private void btnClose_Click(object sender, EventArgs e)
		{
			// If we currently are in DrawTime filter, we just close this and return to normal playback.
			// Propagate to PlayerScreen which will report to ScreenManager.
			m_PlayerScreenUIHandler.ScreenUI_CloseAsked();
		}
		private void PanelVideoControls_MouseEnter(object sender, EventArgs e)
		{
			// Set focus to enable mouse scroll
			panelVideoControls.Focus();
		}
		#endregion
		
		#region Misc private helpers
		private void OnPoke()
		{
			//------------------------------------------------------------------------------
			// This function is a hub event handler for all button press, mouse clicks, etc.
			// Signal itself as the active screen to the ScreenManager
			//---------------------------------------------------------------------
			
			m_PlayerScreenUIHandler.ScreenUI_SetAsActiveScreen();
			
			// 1. Ensure no DrawingText is in edit mode.
			m_FrameServer.Metadata.AllDrawingTextToNormalMode();

			m_ActiveTool = m_ActiveTool.KeepToolFrameChanged ? m_ActiveTool : m_PointerTool;
			if(m_ActiveTool == m_PointerTool)
			{
				SetCursor(m_PointerTool.GetCursor(-1));
			}
			
			// 3. Dock Keyf panel if nothing to see.
			if (m_FrameServer.Metadata.Count < 1)
			{
				DockKeyframePanel(true);
			}
		}
		private string TimeStampsToTimecode(long _iTimeStamp, TimeCodeFormat _timeCodeFormat, bool _bSynched)
		{
			//-------------------------
			// Input    : TimeStamp (might be a duration. If starting ts isn't 0, it should already be shifted.)
			// Output   : time in a specific format
			//-------------------------

			if(!m_FrameServer.Loaded)
                return "0";

			TimeCodeFormat tcf;
			if (_timeCodeFormat == TimeCodeFormat.Unknown)
			{
				tcf = m_PrefManager.TimeCodeFormat;
			}
			else
			{
				tcf = _timeCodeFormat;
			}

			long iTimeStamp;
			if (_bSynched)
			{
				iTimeStamp = _iTimeStamp - m_iSyncPosition;
			}
			else
			{
				iTimeStamp = _iTimeStamp;
			}

			// timestamp to milliseconds. (Needed for most formats)
			double fSeconds = (double)iTimeStamp / m_FrameServer.VideoReader.Info.AverageTimeStampsPerSeconds;
			double fMilliseconds = (fSeconds * 1000) / m_fHighSpeedFactor;
			bool bShowThousandth = m_fHighSpeedFactor *  m_FrameServer.VideoReader.Info.FramesPerSeconds >= 100;
			
			string outputTimeCode;
			switch (tcf)
			{
				case TimeCodeFormat.ClassicTime:
					outputTimeCode = TimeHelper.MillisecondsToTimecode(fMilliseconds, bShowThousandth, true);
					break;
				case TimeCodeFormat.Frames:
					outputTimeCode = String.Format("{0}", (int)((double)iTimeStamp / m_FrameServer.VideoReader.Info.AverageTimeStampsPerFrame) + 1);
					break;
				case TimeCodeFormat.Milliseconds:
					outputTimeCode = String.Format("{0}", (int)Math.Round(fMilliseconds));
					break;
				case TimeCodeFormat.TenThousandthOfHours:
					// 1 Ten Thousandth of Hour = 360 ms.
					double fTth = fMilliseconds / 360.0;
					outputTimeCode = String.Format("{0}:{1:00}", (int)fTth, Math.Floor((fTth - (int)fTth)*100));
					break;
				case TimeCodeFormat.HundredthOfMinutes:
					// 1 Hundredth of minute = 600 ms.
					double fCtm = fMilliseconds / 600.0;
					outputTimeCode = String.Format("{0}:{1:00}", (int)fCtm, Math.Floor((fCtm - (int)fCtm) * 100));
					break;
				case TimeCodeFormat.TimeAndFrames:
					String timeString = TimeHelper.MillisecondsToTimecode(fMilliseconds, bShowThousandth, true);
					String frameString;
					if (m_FrameServer.VideoReader.Info.AverageTimeStampsPerFrame != 0)
					{
						frameString = String.Format("{0}", (int)((double)iTimeStamp / m_FrameServer.VideoReader.Info.AverageTimeStampsPerFrame) + 1);
					}
					else
					{
						frameString = String.Format("0");
					}
					outputTimeCode = String.Format("{0} ({1})", timeString, frameString);
					break;
				case TimeCodeFormat.Timestamps:
					outputTimeCode = String.Format("{0}", (int)iTimeStamp);
					break;
				default :
					outputTimeCode = TimeHelper.MillisecondsToTimecode(fMilliseconds, bShowThousandth, true);
					break;
			}

			return outputTimeCode;
		}
		private void DoDrawingUndrawn()
		{
			//--------------------------------------------------------
			// this function is called after we undo a drawing action.
			// Called from CommandAddDrawing.Unexecute().
			//--------------------------------------------------------
			m_ActiveTool = m_ActiveTool.KeepToolFrameChanged ? m_ActiveTool : m_PointerTool;
			if(m_ActiveTool == m_PointerTool)
			{
				SetCursor(m_PointerTool.GetCursor(0));
			}
		}
		private void UpdateFramesMarkers()
		{
			// Updates the markers coordinates and redraw the trkFrame.
			trkFrame.UpdateMarkers(m_FrameServer.Metadata);
			trkFrame.Invalidate();
		}
		private void ShowBorder(bool _bShow)
		{
			m_bShowImageBorder = _bShow;
			DoInvalidate();
		}
		private void DrawImageBorder(Graphics _canvas)
		{
			// Draw the border around the screen to mark it as selected.
			// Called back from main drawing routine.
			_canvas.DrawRectangle(m_PenImageBorder, 0, 0, pbSurfaceScreen.Width - m_PenImageBorder.Width, pbSurfaceScreen.Height - m_PenImageBorder.Width);
		}
		private void DisablePlayAndDraw()
		{
			StopPlaying();
			m_ActiveTool = m_PointerTool;
			SetCursor(m_PointerTool.GetCursor(0));
			DisableMagnifier();
			UnzoomDirectZoom(false);
			m_FrameServer.Metadata.StopAllTracking();
			CheckCustomDecodingSize(false);
		}
		private Form GetParentForm(Control _parent)
		{
            Form form = _parent as Form;
            if(form != null )
                return form;
    
            if(_parent != null)
                return GetParentForm(_parent.Parent);
            
            return null;
        }
		#endregion

		#region Video Controls

		#region Playback Controls
		public void buttonGotoFirst_Click(object sender, EventArgs e)
		{
			// Jump to start.
			if (m_FrameServer.Loaded)
			{
				OnPoke();
				StopPlaying();
				m_PlayerScreenUIHandler.PlayerScreenUI_PauseAsked();
				
				m_iFramesToDecode = 1;
				ShowNextFrame(m_iSelStart, true);
				
				UpdatePositionUI();
				ActivateKeyframe(m_iCurrentPosition);
			}
		}
		public void buttonGotoPrevious_Click(object sender, EventArgs e)
		{
			if (m_FrameServer.Loaded)
			{
				OnPoke();
				StopPlaying();
				m_PlayerScreenUIHandler.PlayerScreenUI_PauseAsked();
				
				//---------------------------------------------------------------------------
				// Si on est en dehors de la zone primaire, ou qu'on va en sortir,
				// se replacer au d�but de celle-ci.
				//---------------------------------------------------------------------------
				if ((m_iCurrentPosition <= m_iSelStart) || (m_iCurrentPosition > m_iSelEnd))
				{
					m_iFramesToDecode = 1;
					ShowNextFrame(m_iSelStart, true);
				}
				else
				{
					long oldPos = m_iCurrentPosition;
					m_iFramesToDecode = -1;
					ShowNextFrame(-1, true);
					
					// If it didn't work, try going back two frames to unstuck the situation.
					// Todo: check if we're going to endup outside the working zone ?
					if(m_iCurrentPosition == oldPos)
					{
						log.Debug("Seeking to previous frame did not work. Moving backward 2 frames.");
						m_iFramesToDecode = -2;
						ShowNextFrame(-1, true);
					}
						
					// Reset to normal.
					m_iFramesToDecode = 1;
				}
				
				UpdatePositionUI();
				ActivateKeyframe(m_iCurrentPosition);
			}
			
		}
		private void buttonPlay_Click(object sender, EventArgs e)
		{
			//----------------------------------------------------------------------------
			// L'appui sur le bouton play ne fait qu'activer ou d�sactiver le Timer
			// La lecture est ensuite automatique et c'est dans la fonction du Timer
			// que l'on g�re la NextFrame � afficher en fonction du ralentit,
			// du mode de bouclage etc...
			//----------------------------------------------------------------------------
			if (m_FrameServer.Loaded)
			{
				OnPoke();
				OnButtonPlay();
			}
		}
		public void buttonGotoNext_Click(object sender, EventArgs e)
		{
			if (m_FrameServer.Loaded)
			{
				OnPoke();
				StopPlaying();
				m_PlayerScreenUIHandler.PlayerScreenUI_PauseAsked();
				m_iFramesToDecode = 1;

				// If we are outside the primary zone or going to get out, seek to start.
				// We also only do the seek if we are after the m_iStartingPosition,
				// Sometimes, the second frame will have a time stamp inferior to the first,
				// which sort of breaks our sentinels.
				if (((m_iCurrentPosition < m_iSelStart) || (m_iCurrentPosition >= m_iSelEnd)) &&
				    (m_iCurrentPosition >= m_iStartingPosition))
				{
					ShowNextFrame(m_iSelStart, true);
				}
				else
				{
					ShowNextFrame(-1, true);
				}

				UpdatePositionUI();
				ActivateKeyframe(m_iCurrentPosition);
			}
			
		}
		public void buttonGotoLast_Click(object sender, EventArgs e)
		{
			if (m_FrameServer.Loaded)
			{
				OnPoke();
				StopPlaying();
				m_PlayerScreenUIHandler.PlayerScreenUI_PauseAsked();

				m_iFramesToDecode = 1;
				ShowNextFrame(m_iSelEnd, true);

				UpdatePositionUI();
				ActivateKeyframe(m_iCurrentPosition);
			}
		}
		public void OnButtonPlay()
		{
			//--------------------------------------------------------------
			// This function is accessed from ScreenManager.
			// Eventually from a worker thread. (no SetAsActiveScreen here).
			//--------------------------------------------------------------
			if (m_FrameServer.Loaded)
			{
				if (m_bIsCurrentlyPlaying)
				{
					// Go into Pause mode.
					StopPlaying();
					m_PlayerScreenUIHandler.PlayerScreenUI_PauseAsked();
					buttonPlay.Image = Resources.liqplay17;
					ActivateKeyframe(m_iCurrentPosition);
					ToastPause();
				}
				else
				{
					// Go into Play mode
					buttonPlay.Image = Resources.liqpause6;
					StartMultimediaTimer((int)GetPlaybackFrameInterval());
				}
			}
		}
		public void Common_MouseWheel(object sender, MouseEventArgs e)
		{
			// MouseWheel was recorded on one of the controls.
			int iScrollOffset = e.Delta * SystemInformation.MouseWheelScrollLines / 120;

			if(InteractiveFiltering)
			{
                if(m_InteractiveEffect.MouseWheel != null)
                {
                    m_InteractiveEffect.MouseWheel(iScrollOffset);
                    DoInvalidate();
                }
                return;
			}
			
			if ((ModifierKeys & Keys.Control) == Keys.Control)
			{
				if (iScrollOffset > 0)
					IncreaseDirectZoom();
				else
					DecreaseDirectZoom();
			}
			else if((ModifierKeys & Keys.Alt) == Keys.Alt)
			{
			    if (iScrollOffset > 0)
					IncreaseSyncAlpha();
				else
					DecreaseSyncAlpha();
			}
			else
			{
				if (iScrollOffset > 0)
				{
					buttonGotoNext_Click(null, EventArgs.Empty);
				}
				else
				{
                    // Shift + Left on first => loop backward.
				    if (((ModifierKeys & Keys.Shift) == Keys.Shift) && m_iCurrentPosition <= m_iSelStart)
						buttonGotoLast_Click(null, EventArgs.Empty);
					else
						buttonGotoPrevious_Click(null, EventArgs.Empty);
				}
			}
		}
		#endregion

		#region Working Zone Selection
		private void trkSelection_SelectionChanging(object sender, EventArgs e)
		{
			if (m_FrameServer.Loaded)
			{
				StopPlaying();
				m_PlayerScreenUIHandler.PlayerScreenUI_PauseAsked();

				// Update selection timestamps and labels.
				UpdateSelectionDataFromControl();
				UpdateSelectionLabels();

				// Update the frame tracker internal timestamps (including position if needed).
				trkFrame.Remap(m_iSelStart, m_iSelEnd);
			}
		}
		private void trkSelection_SelectionChanged(object sender, EventArgs e)
		{
			// Actual update.
			if (m_FrameServer.Loaded)
			{
				UpdateSelectionDataFromControl();
				UpdateWorkingZone(false);

				AfterSelectionChanged();
			}
		}
		private void trkSelection_TargetAcquired(object sender, EventArgs e)
		{
			// User clicked inside selection: Jump to position.
			if (m_FrameServer.Loaded)
			{
				OnPoke();
				StopPlaying();
				m_PlayerScreenUIHandler.PlayerScreenUI_PauseAsked();
				m_iFramesToDecode = 1;

				ShowNextFrame(trkSelection.SelPos, true);
				m_iCurrentPosition = trkSelection.SelPos + trkSelection.Minimum;
				
				UpdatePositionUI();
				ActivateKeyframe(m_iCurrentPosition);
			}
			
		}
		private void btn_HandlersLock_Click(object sender, EventArgs e)
		{
			// Lock the selection handlers.
			if (m_FrameServer.Loaded)
			{
				m_bHandlersLocked = !m_bHandlersLocked;
				trkSelection.SelLocked = m_bHandlersLocked;

				// Update UI accordingly.
				if (m_bHandlersLocked)
				{
					btn_HandlersLock.Image = Resources.primselec_locked3;
					toolTips.SetToolTip(btn_HandlersLock, ScreenManagerLang.LockSelectionUnlock);
				}
				else
				{
					btn_HandlersLock.Image = Resources.primselec_unlocked3;
					toolTips.SetToolTip(btn_HandlersLock, ScreenManagerLang.LockSelectionLock);
				}
			}
		}
		private void btnSetHandlerLeft_Click(object sender, EventArgs e)
		{
			// Set the left handler of the selection at the current frame.
			if (m_FrameServer.Loaded && !m_bHandlersLocked)
			{
				trkSelection.SelStart = m_iCurrentPosition;
				UpdateSelectionDataFromControl();
				UpdateSelectionLabels();
				trkFrame.Remap(m_iSelStart,m_iSelEnd);
				UpdateWorkingZone(false);
				
				AfterSelectionChanged();
			}
		}
		private void btnSetHandlerRight_Click(object sender, EventArgs e)
		{
			// Set the right handler of the selection at the current frame.
			if (m_FrameServer.Loaded && !m_bHandlersLocked)
			{
				trkSelection.SelEnd = m_iCurrentPosition;
				UpdateSelectionDataFromControl();
				UpdateSelectionLabels();
				trkFrame.Remap(m_iSelStart,m_iSelEnd);
				UpdateWorkingZone(false);
				
				AfterSelectionChanged();
			}
		}
		private void btnHandlersReset_Click(object sender, EventArgs e)
		{
			// Reset both selection sentinels to their max values.
			if (m_FrameServer.Loaded && !m_bHandlersLocked)
			{
				trkSelection.Reset();
				UpdateSelectionDataFromControl();
				
				// We need to force the reloading of all frames.
				UpdateWorkingZone(true);
				
				AfterSelectionChanged();
			}
		}
		
		private void UpdateFramePrimarySelection()
		{
			//--------------------------------------------------------------
			// Update the visible image to reflect the new selection.
			// Checks that the previous current frame is still within selection,
			// jumps to closest sentinel otherwise.
			//--------------------------------------------------------------
			
			if (m_FrameServer.VideoReader.DecodingMode == VideoDecodingMode.Caching)
			{
			    if(m_FrameServer.VideoReader.Current == null)
			        ShowNextFrame(m_iSelStart, true);
			    else
                    ShowNextFrame(m_FrameServer.VideoReader.Current.Timestamp, true);
			}
			else if (m_iCurrentPosition < m_iSelStart || m_iCurrentPosition > m_iSelEnd)
			{
				m_iFramesToDecode = 1;
				if (m_iCurrentPosition < m_iSelStart)
					ShowNextFrame(m_iSelStart, true);
				else
					ShowNextFrame(m_iSelEnd, true);
			}

			UpdatePositionUI();
		}
		private void UpdateSelectionLabels()
		{
		    if(m_FrameServer.Loaded)
		    {
                lblSelStartSelection.Text = ScreenManagerLang.lblSelStartSelection_Text + " : " + TimeStampsToTimecode(m_iSelStart - m_iStartingPosition, m_PrefManager.TimeCodeFormat, false);
                lblSelDuration.Text = ScreenManagerLang.lblSelDuration_Text + " : " + TimeStampsToTimecode(m_iSelDuration, m_PrefManager.TimeCodeFormat, false);
		    }
		}
		private void UpdateSelectionDataFromControl()
		{
			// Update WorkingZone data according to control.
			if ((m_iSelStart != trkSelection.SelStart) || (m_iSelEnd != trkSelection.SelEnd))
			{
				m_iSelStart = trkSelection.SelStart;
				m_iSelEnd = trkSelection.SelEnd;
				m_iSelDuration = m_iSelEnd - m_iSelStart + m_FrameServer.VideoReader.Info.AverageTimeStampsPerFrame;
			}
		}
		private void AfterSelectionChanged()
		{
			// Update everything as if we moved the handlers manually.
			m_FrameServer.Metadata.SelectionStart = m_iSelStart;
			
			UpdateFramesMarkers();
			
			OnPoke();
			m_PlayerScreenUIHandler.PlayerScreenUI_SelectionChanged(false);

			// Update current image and keyframe  status.
			UpdateFramePrimarySelection();
			EnableDisableKeyframes();
			ActivateKeyframe(m_iCurrentPosition);	
		}
		#endregion
		
		#region Frame Tracker
		private void trkFrame_PositionChanging(object sender, PositionChangedEventArgs e)
		{
			// This one should only fire during analysis mode.
			if (m_FrameServer.Loaded)
			{
				// Update image but do not touch cursor, as the user is manipulating it.
				// If the position needs to be adjusted to an actual timestamp, it'll be done later.
				StopPlaying();
				UpdateFrameCurrentPosition(false);
				UpdateCurrentPositionLabel();
				
				ActivateKeyframe(m_iCurrentPosition);
			}
		}
		private void trkFrame_PositionChanged(object sender, PositionChangedEventArgs e)
		{
			if (m_FrameServer.Loaded)
			{
				OnPoke();
				StopPlaying();
				m_PlayerScreenUIHandler.PlayerScreenUI_PauseAsked();

				// Update image and cursor.
				UpdateFrameCurrentPosition(true);
				UpdateCurrentPositionLabel();
				ActivateKeyframe(m_iCurrentPosition);

				// Update WorkingZone hairline.
				trkSelection.SelPos =  m_iCurrentPosition;
				trkSelection.Invalidate();
			}
		}
		private void UpdateFrameCurrentPosition(bool _bUpdateNavCursor)
		{
			// Displays the image corresponding to the current position within working zone.
			// Trigerred by user (or first load). i.e: cursor moved, show frame.
			if (m_FrameServer.VideoReader.DecodingMode != VideoDecodingMode.Caching)
				this.Cursor = Cursors.WaitCursor;

			m_iCurrentPosition = trkFrame.Position;
			m_iFramesToDecode = 1;
			ShowNextFrame(m_iCurrentPosition, true);

            // The following may readjust the cursor in case the mouse wasn't on a valid timestamp value.
			if (_bUpdateNavCursor)
				UpdatePositionUI();

			if (m_FrameServer.VideoReader.DecodingMode != VideoDecodingMode.Caching)
				this.Cursor = Cursors.Default;
		}
		private void UpdateCurrentPositionLabel()
		{
		    // Note: among other places, this is run inside the playloop.
			// Position is relative to working zone.
			string timecode = TimeStampsToTimecode(m_iCurrentPosition - m_iSelStart, m_PrefManager.TimeCodeFormat, m_bSynched);
			lblTimeCode.Text = string.Format("{0} : {1}", ScreenManagerLang.lblTimeCode_Text, timecode);
		}
		private void UpdatePositionUI()
		{
			// Update markers and label for position.
			
			//trkFrame.UpdateCacheSegmentMarker(m_FrameServer.VideoReader.Cache.Segment);
			trkFrame.Position = m_iCurrentPosition;
            trkFrame.UpdateCacheSegmentMarker(m_FrameServer.VideoReader.PreBufferingSegment);
			trkFrame.Invalidate();
            trkSelection.SelPos = m_iCurrentPosition;
			trkSelection.Invalidate();
			UpdateCurrentPositionLabel();
			RepositionSpeedControl();
		}
		#endregion

		#region Speed Slider
		private void sldrSpeed_ValueChanged(object sender, EventArgs e)
		{
			m_fSlowmotionPercentage = sldrSpeed.Value > 0 ? sldrSpeed.Value : 1;
			
			if (m_FrameServer.Loaded)
			{
				// Reset timer with new value.
				if (m_bIsCurrentlyPlaying)
				{
					StopMultimediaTimer();
					StartMultimediaTimer((int)GetPlaybackFrameInterval());
				}

				// Impacts synchro.
				m_PlayerScreenUIHandler.PlayerScreenUI_SpeedChanged(true);
			}

			UpdateSpeedLabel();
		}
		private void sldrSpeed_KeyDown(object sender, KeyEventArgs e)
		{
			// Increase/Decrease speed on UP/DOWN Arrows.
			if (m_FrameServer.Loaded)
			{
				int jumpFactor = 25;
				if( (ModifierKeys & Keys.Control) == Keys.Control)
				{
					jumpFactor = 1;
				}
				else if((ModifierKeys & Keys.Shift) == Keys.Shift)
				{
					jumpFactor = 10;
				}
			
				if (e.KeyCode == Keys.Down)
				{
					sldrSpeed.ForceValue(jumpFactor * ((sldrSpeed.Value-1) / jumpFactor));
					e.Handled = true;
				}
				else if (e.KeyCode == Keys.Up)
				{
					sldrSpeed.ForceValue(jumpFactor * ((sldrSpeed.Value / jumpFactor) + 1));
					e.Handled = true;
				}
			}
		}
		private void lblSpeedTuner_DoubleClick(object sender, EventArgs e)
		{
			// Double click on the speed label : Back to 100%
			sldrSpeed.ForceValue(sldrSpeed.StickyValue);
		}
		private void UpdateSpeedLabel()
		{
			if(m_fHighSpeedFactor != 1.0)
			{
				double fRealtimePercentage = (double)m_fSlowmotionPercentage / m_fHighSpeedFactor;
				lblSpeedTuner.Text = String.Format("{0} {1:0.00}%", ScreenManagerLang.lblSpeedTuner_Text, fRealtimePercentage);
			}
			else
			{
				if(m_fSlowmotionPercentage % 1 == 0)
				{
					lblSpeedTuner.Text = ScreenManagerLang.lblSpeedTuner_Text + " " + m_fSlowmotionPercentage + "%";
				}
				else
				{
					// Special case when the speed percentage is coming from the other screen and is not an integer.
					lblSpeedTuner.Text = String.Format("{0} {1:0.00}%", ScreenManagerLang.lblSpeedTuner_Text, m_fSlowmotionPercentage);
				}
			}			
		}
		private void RepositionSpeedControl()
		{
            lblSpeedTuner.Left = lblTimeCode.Left + lblTimeCode.Width + 8;
            sldrSpeed.Left = lblSpeedTuner.Left + lblSpeedTuner.Width + 8;
		}
		#endregion

		#region Loop Mode
		private void buttonPlayingMode_Click(object sender, EventArgs e)
		{
			// Playback mode ('Once' or 'Loop').
			if (m_FrameServer.Loaded)
			{
				OnPoke();

				if (m_ePlayingMode == PlayingMode.Once)
				{
					m_ePlayingMode = PlayingMode.Loop;
				}
				else if (m_ePlayingMode == PlayingMode.Loop)
				{
					m_ePlayingMode = PlayingMode.Once;
				}
				
				UpdatePlayingModeButton();
			}
		}
		private void UpdatePlayingModeButton()
		{
			if (m_ePlayingMode == PlayingMode.Once)
			{
				buttonPlayingMode.Image = Resources.playmodeonce;
				toolTips.SetToolTip(buttonPlayingMode, ScreenManagerLang.ToolTip_PlayingMode_Once);		
			}
			else if(m_ePlayingMode == PlayingMode.Loop)
			{
				buttonPlayingMode.Image = Resources.playmodeloop;
				toolTips.SetToolTip(buttonPlayingMode, ScreenManagerLang.ToolTip_PlayingMode_Loop);	
			}
		}
		#endregion

		#endregion

		#region Auto Stretch & Manual Resize
		private void StretchSqueezeSurface()
		{
		    // Compute the rendering size, and the corresponding optimal decoding size.
		    // We don't ask the VideoReader to update its decoding size here.
		    // (We might want to wait the end of a resizing process for example.).
		    // Similarly, we don't update the rendering zoom factor, so that during resizing process,
		    // the zoom window is still computed based on the current decoding size.
		    
            if (!m_FrameServer.Loaded)
                return;

            double targetStretch = m_FrameServer.CoordinateSystem.Stretch;
            
            // If we have been forced to a different stretch (due to application resizing or minimizing), 
            // make sure we aim for the user's last requested value.
            if(!m_fill && m_lastUserStretch != m_viewportManipulator.Stretch)
                targetStretch = m_lastUserStretch;
            
            // Stretch factor, zoom, or container size have been updated, update the rendering and decoding sizes.
            // During the process, stretch and fill may be forced to different values.
            m_viewportManipulator.Manipulate(panelCenter.Size, targetStretch, m_fill, m_FrameServer.CoordinateSystem.Zoom, m_bEnableCustomDecodingSize);
            pbSurfaceScreen.Size = m_viewportManipulator.RenderingSize;
            pbSurfaceScreen.Location = m_viewportManipulator.RenderingLocation;
            m_FrameServer.CoordinateSystem.Stretch = m_viewportManipulator.Stretch;
            
            ReplaceResizers();
		}
		private void ReplaceResizers()
		{
			ImageResizerSE.Left = pbSurfaceScreen.Right - (ImageResizerSE.Width / 2);
			ImageResizerSE.Top = pbSurfaceScreen.Bottom - (ImageResizerSE.Height / 2);
            
			ImageResizerSW.Left = pbSurfaceScreen.Left - (ImageResizerSW.Width / 2);
			ImageResizerSW.Top = pbSurfaceScreen.Bottom - (ImageResizerSW.Height / 2);
            
			ImageResizerNE.Left = pbSurfaceScreen.Right - (ImageResizerNE.Width / 2);
			ImageResizerNE.Top = pbSurfaceScreen.Top - (ImageResizerNE.Height / 2);

			ImageResizerNW.Left = pbSurfaceScreen.Left - ImageResizerNW.Width/2;
			ImageResizerNW.Top = pbSurfaceScreen.Top - ImageResizerNW.Height/2;
		}
		private void ToggleImageFillMode()
		{
			if (!m_fill)
			{
				m_fill = true;
			}
			else
			{
				// If the image doesn't fit in the container, we stay in fill mode.
				if (m_FrameServer.CoordinateSystem.Stretch >= 1)
				{
					m_FrameServer.CoordinateSystem.Stretch = 1;
					m_fill = false;
				}
			}
			
			ResizeUpdate(true);
		}
		private void ImageResizerSE_MouseMove(object sender, MouseEventArgs e)
		{
			if (e.Button != MouseButtons.Left)
			    return;
			
			int iTargetHeight = (ImageResizerSE.Top - pbSurfaceScreen.Top + e.Y);
			int iTargetWidth = (ImageResizerSE.Left - pbSurfaceScreen.Left + e.X);
			ManualResizeImage(iTargetWidth, iTargetHeight);
		}
		private void ImageResizerSW_MouseMove(object sender, MouseEventArgs e)
		{
			if (e.Button == MouseButtons.Left)
			{
				int iTargetHeight = (ImageResizerSW.Top - pbSurfaceScreen.Top + e.Y);
				int iTargetWidth = pbSurfaceScreen.Width + (pbSurfaceScreen.Left - (ImageResizerSW.Left + e.X));
				ManualResizeImage(iTargetWidth, iTargetHeight);
			}
		}
		private void ImageResizerNW_MouseMove(object sender, MouseEventArgs e)
		{
			if (e.Button == MouseButtons.Left)
			{
				int iTargetHeight = pbSurfaceScreen.Height + (pbSurfaceScreen.Top - (ImageResizerNW.Top + e.Y));
				int iTargetWidth = pbSurfaceScreen.Width + (pbSurfaceScreen.Left - (ImageResizerNW.Left + e.X));
				ManualResizeImage(iTargetWidth, iTargetHeight);
			}
		}
		private void ImageResizerNE_MouseMove(object sender, MouseEventArgs e)
		{
			if (e.Button == MouseButtons.Left)
			{
				int iTargetHeight = pbSurfaceScreen.Height + (pbSurfaceScreen.Top - (ImageResizerNE.Top + e.Y));
				int iTargetWidth = (ImageResizerNE.Left - pbSurfaceScreen.Left + e.X);
				ManualResizeImage(iTargetWidth, iTargetHeight);
			}
		}
		private void ManualResizeImage(int _iTargetWidth, int _iTargetHeight)
		{
		    Size targetSize = new Size(_iTargetWidth, _iTargetHeight);
		    if(!targetSize.FitsIn(panelCenter.Size))
		        return;
		    
		    if(!m_bManualSqueeze && !m_FrameServer.VideoReader.Info.AspectRatioSize.FitsIn(targetSize))
		        return;
		    
		    // Area of the original size is sticky on the inside.
		    if(!m_FrameServer.VideoReader.Info.AspectRatioSize.FitsIn(targetSize) && 
		       (m_FrameServer.VideoReader.Info.AspectRatioSize.Width - _iTargetWidth < 40 &&
		        m_FrameServer.VideoReader.Info.AspectRatioSize.Height - _iTargetHeight < 40))
		    {
		        _iTargetWidth = m_FrameServer.VideoReader.Info.AspectRatioSize.Width;
		        _iTargetHeight = m_FrameServer.VideoReader.Info.AspectRatioSize.Height;
		    }
		    
		    if(!m_MinimalSize.FitsIn(targetSize))
		        return;
		    
			double fHeightFactor = ((_iTargetHeight) / (double)m_FrameServer.VideoReader.Info.AspectRatioSize.Height);
			double fWidthFactor = ((_iTargetWidth) / (double)m_FrameServer.VideoReader.Info.AspectRatioSize.Width);

			m_FrameServer.CoordinateSystem.Stretch = (fWidthFactor + fHeightFactor) / 2;
			m_fill = false;
			m_lastUserStretch = m_FrameServer.CoordinateSystem.Stretch;

			ResizeUpdate(false);
		}
		private void Resizers_MouseDoubleClick(object sender, MouseEventArgs e)
		{
			ToggleImageFillMode();
		}
		private void Resizers_MouseUp(object sender, MouseEventArgs e)
		{
            ResizeUpdate(true);
		}
		private void ResizeUpdate(bool _finished)
		{
		    if(!m_FrameServer.Loaded)
		        return;
		    
            StretchSqueezeSurface();

            if(_finished)
            {
                // Update the decoding size. (May clear and restart the prebuffering).
                if(m_FrameServer.VideoReader.CanChangeDecodingSize)
                {
                    m_FrameServer.VideoReader.ChangeDecodingSize(m_viewportManipulator.DecodingSize);
                    m_FrameServer.CoordinateSystem.SetRenderingZoomFactor(m_viewportManipulator.RenderingZoomFactor);
                }
                m_FrameServer.Metadata.ResizeFinished();
                RefreshImage();
            }
            else
            {
                DoInvalidate();
            }
		}
		private void CheckCustomDecodingSize(bool _forceDisable)
		{
            // Enable or disable custom decoding size depending on current state.
            // Custom decoding size is not compatible with tracking.
            // The boolean will later be used each time we attempt to change decoding size in StretchSqueezeSurface.
            // This is not concerned with decoding mode (prebuffering, caching, etc.) as this will be checked inside the reader.
            bool wasCustomDecodingSize = m_bEnableCustomDecodingSize;
            m_bEnableCustomDecodingSize = !m_FrameServer.Metadata.IsTracking && !_forceDisable;
            
            if(wasCustomDecodingSize && !m_bEnableCustomDecodingSize)
            {
                m_FrameServer.VideoReader.DisableCustomDecodingSize();
            }
            else if(!wasCustomDecodingSize && m_bEnableCustomDecodingSize)
            {
                ResizeUpdate(true);
            }
            
            log.DebugFormat("CheckCustomDecodingSize. was:{0}, is:{1}", wasCustomDecodingSize, m_bEnableCustomDecodingSize);
		}
		#endregion
		
		#region Timers & Playloop
		private void StartMultimediaTimer(int _interval)
		{
		    //log.DebugFormat("starting playback timer at {0} ms interval.", _interval);
            ActivateKeyframe(-1);
            m_DropWatcher.Restart();
            m_LoopWatcher.Restart();
            
            Application.Idle += Application_Idle;
            m_FrameServer.VideoReader.BeforePlayloop();

            int userCtx = 0;
			m_IdMultimediaTimer = timeSetEvent(_interval, _interval, m_TimerEventHandler, ref userCtx, TIME_PERIODIC | TIME_KILL_SYNCHRONOUS);
			m_bIsCurrentlyPlaying = true;
		}
		private void StopMultimediaTimer()
		{
			if (m_IdMultimediaTimer != 0)
				timeKillEvent(m_IdMultimediaTimer);
			m_IdMultimediaTimer = 0;
			m_bIsCurrentlyPlaying = false;
			Application.Idle -= Application_Idle;
			
			log.DebugFormat("Rendering drops ratio: {0:0.00}", m_DropWatcher.Ratio);
			log.DebugFormat("Average rendering loop time: {0:0.000}ms", m_LoopWatcher.Average);
		}
		private void MultimediaTimer_Tick(uint id, uint msg, ref int userCtx, int rsv1, int rsv2)
		{
		    if(!m_FrameServer.Loaded)
                return;
		    
            // We cannot change the pointer to current here in case the UI is painting it,
            // so we will pass the number of drops along to the rendering.
            // The rendering will then ask for an update of the pointer to current, skipping as
            // many frames we missed during the interval.
            lock(m_TimingSync)
            {
                if(!m_bIsBusyRendering)
                {
                    int drops = m_RenderingDrops;
                    BeginInvoke((Action) delegate {Rendering_Invoked(drops);});
                    m_bIsBusyRendering = true;
                    m_RenderingDrops = 0;
                    m_DropWatcher.AddDropStatus(false);
                }
                else
                {
                    m_RenderingDrops++;
                    m_DropWatcher.AddDropStatus(true);
                }
            }
		}
		private void Rendering_Invoked(int _missedFrames)
		{
		    // This is in UI thread space.
		    // Rendering in the context of continuous playback (play loop).
            m_TimeWatcher.Restart();

            int skip = m_FrameServer.Metadata.IsTracking ? 0 : _missedFrames;
            
		    long estimateNext = m_iCurrentPosition + ((skip + 1) * m_FrameServer.VideoReader.Info.AverageTimeStampsPerFrame);
			if (estimateNext > m_iSelEnd)
			{
			    EndOfFile();
			}
			else
			{
			    // This may be slow (several ms) due to delete call when dequeuing the pre-buffer. To investigate.
                bool hasMore = m_FrameServer.VideoReader.MoveNext(skip, false);
                //m_TimeWatcher.LogTime("Moved to next frame.");
                
                // In case the frame wasn't available in the pre-buffer, don't render anything.
                // This means if we missed the previous frame because the UI was busy, we won't 
                // render it now either. On the other hand, it means we will have less chance to
                // miss the next frame while trying to render an already outdated one.
                // We must also "unreset" the rendering drop counter, since we didn't actually render the frame.
                if(m_FrameServer.VideoReader.Drops > 0)
                {
                    if(m_FrameServer.VideoReader.Drops > m_MaxDecodingDrops)
                    {
                        log.DebugFormat("Failsafe triggered on Decoding Drops ({0})", m_FrameServer.VideoReader.Drops);
                        ForceSlowdown();
                    }
                    else
                    {
                       lock(m_TimingSync)
                            m_RenderingDrops = _missedFrames;
                    }
                }
                else if(m_FrameServer.VideoReader.Current != null)
                {
                    DoInvalidate();
                    m_iCurrentPosition = m_FrameServer.VideoReader.Current.Timestamp;
                    ComputeOrStopTracking(skip == 0);
                    
                    // This causes Invalidates and will postpone the idle event.
                    // Update UI. For speed purposes, we don't update Selection Tracker hairline.
                    //trkFrame.UpdateCacheSegmentMarker(m_FrameServer.VideoReader.Cache.Segment);
                    trkFrame.Position = m_iCurrentPosition;
                    trkFrame.UpdateCacheSegmentMarker(m_FrameServer.VideoReader.PreBufferingSegment);
                    trkFrame.Invalidate();
                    UpdateCurrentPositionLabel();
                    
                    ReportForSyncMerge();
                    //m_TimeWatcher.LogTime("All rendiring operations posted.");
                }
                
                /*if (skip > m_MaxRenderingDrops)
				{
				    log.DebugFormat("Failsafe triggered on Rendering Drops ({0})", skip);
				    ForceSlowdown();
				}*/
			}
			//m_TimeWatcher.LogTime("Exiting Rendering_Invodked.");
		}
		private void PlayLoop_Invoked()
		{
		    // DEPRECATED.
		    // Kept for documentation purpose for a while.
		    
			// Runs in the UI thread.
		    
			long estimateNext = m_iCurrentPosition + (m_iFramesToDecode * m_FrameServer.VideoReader.Info.AverageTimeStampsPerFrame);
			if (estimateNext > m_iSelEnd)
			{
			    EndOfFile();
			}
			else if(m_bIsIdle)
            {
			    m_bIsIdle = false;
			    
                // Regular loop.
                ShowNextFrame(-1, true);
                
                if(m_FrameServer.VideoReader.Drops > m_MaxDecodingDrops)
                {
                    log.DebugFormat("Failsafe triggered on Decoding Drops ({0})", m_FrameServer.VideoReader.Drops);
                    ForceSlowdown();
                }
                
                UpdatePositionUI();
				m_iFramesToDecode = 1;
            }
			else
			{
			    // The BeginInvoke in timer tick was posted before the form became idle.
			    // Not really the proper way to figure out if we are dropping.
			    if (!m_FrameServer.Metadata.IsTracking)
				{
					m_iFramesToDecode++;
					log.DebugFormat("Rendering Drops: {0}.", m_iFramesToDecode-1);
				}
                
				if (m_iFramesToDecode > m_MaxRenderingDrops)
				{
				    log.DebugFormat("Failsafe triggered on Rendering Drops ({0})", m_iFramesToDecode-1);
				    ForceSlowdown();
				}
			}
			
			log.Debug("Exiting playloop.");
		}
		private void EndOfFile()
		{
		    m_FrameServer.Metadata.StopAllTracking();

            if(m_bSynched)
            {
                StopPlaying();
                ShowNextFrame(m_iSelStart, true);
            }
            else if(m_ePlayingMode == PlayingMode.Loop)
            {
                StopMultimediaTimer();
                bool rewound = ShowNextFrame(m_iSelStart, true);
                
                if(rewound)
                    StartMultimediaTimer((int)GetPlaybackFrameInterval());
                else
                    StopPlaying();
            }
            else
            {
                StopPlaying();
            }
            
            UpdatePositionUI();
			m_iFramesToDecode = 1;
		}
		private void ForceSlowdown()
		{
		    m_FrameServer.VideoReader.ResetDrops();
		    m_iFramesToDecode = 0;
            sldrSpeed.ForceValue(sldrSpeed.Value - sldrSpeed.LargeChange);
		}
		private void ComputeOrStopTracking(bool _contiguous)
		{
		    if(!m_FrameServer.Metadata.IsTracking)
                return;
		    
            // Fixme: Tracking only supports contiguous frames,
            // but this should be the responsibility of the track tool anyway.
            if (!_contiguous)
                m_FrameServer.Metadata.StopAllTracking();
            else
                m_FrameServer.Metadata.PerformTracking(m_FrameServer.VideoReader.Current);

            UpdateFramesMarkers();
            CheckCustomDecodingSize(false);
		}
		private void Application_Idle(object sender, EventArgs e)
		{
		    // This event fires when the window has consumed all its messages.
		    // Forcing the rendering to synchronize with this event allows
		    // the UI to have a chance to process non-rendering related events like
		    // button clicks, mouse move, etc.
		    m_bIsIdle = true;
			lock(m_TimingSync)
		        m_bIsBusyRendering = false;

			m_TimeWatcher.LogTime("Back to idleness");
			//m_TimeWatcher.DumpTimes();
			m_LoopWatcher.AddLoopTime(m_TimeWatcher.RawTime("Back to idleness"));
		}
		private bool ShowNextFrame(long _iSeekTarget, bool _bAllowUIUpdate)
		{
		    // TODO: More refactoring needed.
		    // Eradicate the scheme where we use the _iSeekTarget parameter to mean two things.
		    if(m_bIsCurrentlyPlaying)
		        throw new ThreadStateException("ShowNextFrame called while play loop.");
		    
		    if(!m_FrameServer.VideoReader.Loaded)
		        return false;
		    
		    bool hasMore = false;
		    
		    if(_iSeekTarget < 0)
		        hasMore = m_FrameServer.VideoReader.MoveBy(m_iFramesToDecode, true);
		    else
		        hasMore = m_FrameServer.VideoReader.MoveTo(_iSeekTarget);
		    
            if(m_FrameServer.VideoReader.Current != null)
			{
				m_iCurrentPosition = m_FrameServer.VideoReader.Current.Timestamp;
				
				bool contiguous = _iSeekTarget < 0 && m_iFramesToDecode <= 1;
				ComputeOrStopTracking(contiguous);
				
				if(_bAllowUIUpdate) 
				{
				    //trkFrame.UpdateCacheSegmentMarker(m_FrameServer.VideoReader.Cache.Segment);
				    DoInvalidate();
				}
				
				ReportForSyncMerge();
			}
            
            if(!hasMore)
			{
                // End of working zone reached.
			    m_iCurrentPosition = m_iSelEnd;
				if(_bAllowUIUpdate)
				{
					trkSelection.SelPos = m_iCurrentPosition;
					DoInvalidate();
				}

				m_FrameServer.Metadata.StopAllTracking();
			}
			
			//m_Stopwatch.Stop();
			//log.Debug(String.Format("ShowNextFrame: {0} ms.", m_Stopwatch.ElapsedMilliseconds));
			
			return hasMore;
		}
		private void StopPlaying(bool _bAllowUIUpdate)
		{
			if (!m_FrameServer.Loaded || !m_bIsCurrentlyPlaying)
			    return;
			
			StopMultimediaTimer();

			lock(m_TimingSync)
			{
		        m_bIsBusyRendering = false;
                m_RenderingDrops = 0;
			}

			m_iFramesToDecode = 0;
			
			if (_bAllowUIUpdate)
			{
				buttonPlay.Image = Resources.liqplay17;
				DoInvalidate();
				UpdatePositionUI();
			}
		}
		private void mnuSetCaptureSpeed_Click(object sender, EventArgs e)
		{
			DisplayConfigureSpeedBox(false);
		}
		private void lblTimeCode_DoubleClick(object sender, EventArgs e)
		{
			// Same as mnuSetCaptureSpeed_Click but different location.
			DisplayConfigureSpeedBox(true);
		}
		public void DisplayConfigureSpeedBox(bool _center)
		{
			//--------------------------------------------------------------------
			// Display the dialog box that let the user specify the capture speed.
			// Used to adpat time for high speed cameras.
			//--------------------------------------------------------------------
			if (!m_FrameServer.Loaded)
			    return;
			
			DelegatesPool dp = DelegatesPool.Instance();
			if (dp.DeactivateKeyboardHandler != null)
			{
				dp.DeactivateKeyboardHandler();
			}

			formConfigureSpeed fcs = new formConfigureSpeed(m_FrameServer.VideoReader.Info.FramesPerSeconds, m_fHighSpeedFactor);
			if (_center)
			{
				fcs.StartPosition = FormStartPosition.CenterScreen;
			}
			else
			{
				fcs.StartPosition = FormStartPosition.Manual;
				ScreenManagerKernel.LocateForm(fcs);
			}
			
			if (fcs.ShowDialog() == DialogResult.OK)
			{
				m_fHighSpeedFactor = fcs.SlowFactor;
			}
			
			fcs.Dispose();

			if (dp.ActivateKeyboardHandler != null)
			{
				dp.ActivateKeyboardHandler();
			}

			// Update times.
			UpdateSelectionLabels();
			UpdateCurrentPositionLabel();
			UpdateSpeedLabel();
			m_PlayerScreenUIHandler.PlayerScreenUI_SpeedChanged(true);
			m_FrameServer.Metadata.CalibrationHelper.FramesPerSeconds = m_FrameServer.VideoReader.Info.FramesPerSeconds * m_fHighSpeedFactor;
			DoInvalidate();
		}
		private double GetPlaybackFrameInterval()
		{
			// Returns the playback interval between frames in Milliseconds, taking slow motion slider into account.
			// m_iSlowmotionPercentage must be > 0.
			if (m_FrameServer.Loaded && m_FrameServer.VideoReader.Info.FrameIntervalMilliseconds > 0 && m_fSlowmotionPercentage > 0)
			{
				return (m_FrameServer.VideoReader.Info.FrameIntervalMilliseconds / ((double)m_fSlowmotionPercentage / 100.0));
			}
			else
			{
				return 40;
			}
		}
		private void DeselectionTimer_OnTick(object sender, EventArgs e) 
		{
			// Deselect the currently selected drawing.
			// This is used for drawings that must show extra stuff for being transformed, but we 
			// don't want to show the extra stuff all the time for clarity.
			
			m_FrameServer.Metadata.Deselect();
			log.Debug("Deselection timer fired.");
			m_DeselectionTimer.Stop();
			DoInvalidate();
		}
		#endregion
		
		#region Culture
		private void ReloadMenusCulture()
		{
			// Reload the text for each menu.
			// this is done at construction time and at RefreshUICulture time.
			
			// 1. Default context menu.
			mnuDirectTrack.Text = ScreenManagerLang.mnuTrackTrajectory;
			mnuPlayPause.Text = ScreenManagerLang.mnuPlayPause;
			mnuSetCaptureSpeed.Text = ScreenManagerLang.mnuSetCaptureSpeed;
			mnuSavePic.Text = ScreenManagerLang.Generic_SaveImage;
			mnuSendPic.Text = ScreenManagerLang.mnuSendPic;
			mnuCloseScreen.Text = ScreenManagerLang.mnuCloseScreen;
			
			// 2. Drawings context menu.
			mnuConfigureDrawing.Text = ScreenManagerLang.mnuConfigureDrawing_ColorSize;
			mnuConfigureFading.Text = ScreenManagerLang.mnuConfigureFading;
			mnuConfigureOpacity.Text = ScreenManagerLang.Generic_Opacity;
			mnuTrackTrajectory.Text = ScreenManagerLang.mnuTrackTrajectory;
			mnuGotoKeyframe.Text = ScreenManagerLang.mnuGotoKeyframe;
			mnuDeleteDrawing.Text = ScreenManagerLang.mnuDeleteDrawing;
			
			// 3. Tracking pop menu (Restart, Stop tracking)
			mnuStopTracking.Text = ScreenManagerLang.mnuStopTracking;
			mnuRestartTracking.Text = ScreenManagerLang.mnuRestartTracking;
			mnuDeleteTrajectory.Text = ScreenManagerLang.mnuDeleteTrajectory;
			mnuDeleteEndOfTrajectory.Text = ScreenManagerLang.mnuDeleteEndOfTrajectory;
			mnuConfigureTrajectory.Text = ScreenManagerLang.Generic_Configuration;
			
			// 4. Chrono pop menu (Start, Stop, Hide, etc.)
			mnuChronoConfigure.Text = ScreenManagerLang.Generic_Configuration;
			mnuChronoStart.Text = ScreenManagerLang.mnuChronoStart;
			mnuChronoStop.Text = ScreenManagerLang.mnuChronoStop;
			mnuChronoHide.Text = ScreenManagerLang.mnuChronoHide;
			mnuChronoCountdown.Text = ScreenManagerLang.mnuChronoCountdown;
			mnuChronoDelete.Text = ScreenManagerLang.mnuChronoDelete;
			
			// 5. Magnifier
			foreach(ToolStripMenuItem m in maginificationMenus)
			{
			    double factor = (double)m.Tag;
			    m.Text = String.Format(ScreenManagerLang.mnuMagnification, factor.ToString());
			}
			mnuMagnifierDirect.Text = ScreenManagerLang.mnuMagnifierDirect;
			mnuMagnifierQuit.Text = ScreenManagerLang.mnuMagnifierQuit;
			
			// 6. Spotlight
			mnuDeleteMultiDrawingItem.Text = ScreenManagerLang.mnuDeleteDrawing;
		}
		private void ReloadTooltipsCulture()
		{
			// Video controls
			toolTips.SetToolTip(buttonPlay, ScreenManagerLang.ToolTip_Play);
			toolTips.SetToolTip(buttonGotoPrevious, ScreenManagerLang.ToolTip_Back);
			toolTips.SetToolTip(buttonGotoNext, ScreenManagerLang.ToolTip_Next);
			toolTips.SetToolTip(buttonGotoFirst, ScreenManagerLang.ToolTip_First);
			toolTips.SetToolTip(buttonGotoLast, ScreenManagerLang.ToolTip_Last);
			if (m_ePlayingMode == PlayingMode.Once)
			{
				toolTips.SetToolTip(buttonPlayingMode, ScreenManagerLang.ToolTip_PlayingMode_Once);
			}
			else
			{
				toolTips.SetToolTip(buttonPlayingMode, ScreenManagerLang.ToolTip_PlayingMode_Loop);
			}
			
			// Export buttons
			toolTips.SetToolTip(btnSnapShot, ScreenManagerLang.Generic_SaveImage);
			toolTips.SetToolTip(btnRafale, ScreenManagerLang.ToolTip_Rafale);
			toolTips.SetToolTip(btnDiaporama, ScreenManagerLang.ToolTip_SaveDiaporama);
			toolTips.SetToolTip(btnSaveVideo, ScreenManagerLang.dlgSaveVideoTitle);
			toolTips.SetToolTip(btnPausedVideo, ScreenManagerLang.ToolTip_SavePausedVideo);
			
			// Working zone and sliders.
			if (m_bHandlersLocked)
			{
				toolTips.SetToolTip(btn_HandlersLock, ScreenManagerLang.LockSelectionUnlock);
			}
			else
			{
				toolTips.SetToolTip(btn_HandlersLock, ScreenManagerLang.LockSelectionLock);
			}
			toolTips.SetToolTip(btnSetHandlerLeft, ScreenManagerLang.ToolTip_SetHandlerLeft);
			toolTips.SetToolTip(btnSetHandlerRight, ScreenManagerLang.ToolTip_SetHandlerRight);
			toolTips.SetToolTip(btnHandlersReset, ScreenManagerLang.ToolTip_ResetWorkingZone);
			trkSelection.ToolTip = ScreenManagerLang.ToolTip_trkSelection;
			sldrSpeed.ToolTip = ScreenManagerLang.ToolTip_sldrSpeed;

			// Drawing tools
			foreach(ToolStripItem tsi in stripDrawingTools.Items)
			{
				if(tsi is ToolStripButton)
				{
					AbstractDrawingTool tool = tsi.Tag as AbstractDrawingTool;
					if(tool != null)
					{
						tsi.ToolTipText = tool.DisplayName;
					}
				}
			}
			
			m_btnAddKeyFrame.ToolTipText = ScreenManagerLang.ToolTip_AddKeyframe;
			m_btnShowComments.ToolTipText = ScreenManagerLang.ToolTip_ShowComments;
			m_btnToolPresets.ToolTipText = ScreenManagerLang.ToolTip_ColorProfile;
		}
		#endregion

		#region SurfaceScreen Events
		private void SurfaceScreen_MouseDown(object sender, MouseEventArgs e)
		{
		    if(!m_FrameServer.Loaded)
                return;
                
			m_DeselectionTimer.Stop();
			m_DescaledMouse = m_FrameServer.CoordinateSystem.Untransform(e.Location);
			
			if (e.Button == MouseButtons.Left)
			    SurfaceScreen_LeftDown();
			else if (e.Button == MouseButtons.Right)
			    SurfaceScreen_RightDown();

			DoInvalidate();
		}
		private void SurfaceScreen_LeftDown()
		{
			bool hitMagnifier = false;
            if(m_ActiveTool == m_PointerTool)
                hitMagnifier = m_FrameServer.Metadata.Magnifier.OnMouseDown(m_DescaledMouse);
				
            if(hitMagnifier || InteractiveFiltering)
                return;
            
			if (m_bIsCurrentlyPlaying)
			{
				// MouseDown while playing: Halt the video.
				StopPlaying();
				m_PlayerScreenUIHandler.PlayerScreenUI_PauseAsked();
				ActivateKeyframe(m_iCurrentPosition);
				ToastPause();
			}
			
			m_FrameServer.Metadata.AllDrawingTextToNormalMode();
			
			if (m_ActiveTool == m_PointerTool)
			{
				bool bDrawingHit = false;
				
				// Show the grabbing hand cursor.
				SetCursor(m_PointerTool.GetCursor(1));
				bDrawingHit = m_PointerTool.OnMouseDown(m_FrameServer.Metadata, m_iActiveKeyFrameIndex, m_DescaledMouse, m_iCurrentPosition, m_PrefManager.DefaultFading.Enabled);
			}
			else if (m_ActiveTool == ToolManager.Chrono)
			{
				// Add a Chrono.
				DrawingChrono chrono = (DrawingChrono)m_ActiveTool.GetNewDrawing(m_DescaledMouse, m_iCurrentPosition, m_FrameServer.Metadata.AverageTimeStampsPerFrame);
				m_FrameServer.Metadata.AddChrono(chrono);
				m_ActiveTool = m_PointerTool;
			}
			else if(m_ActiveTool == ToolManager.Spotlight)
			{
			    m_FrameServer.Metadata.SpotlightManager.Add(m_DescaledMouse, m_iCurrentPosition, m_FrameServer.Metadata.AverageTimeStampsPerFrame);
			    m_FrameServer.Metadata.SelectExtraDrawing(m_FrameServer.Metadata.SpotlightManager);
			    //m_ActiveTool = m_ActiveTool.KeepTool ? m_ActiveTool : m_PointerTool;
			}
			else if(m_ActiveTool == ToolManager.AutoNumbers)
			{
			    m_FrameServer.Metadata.AutoNumberManager.Add(m_DescaledMouse, m_iCurrentPosition, m_FrameServer.Metadata.AverageTimeStampsPerFrame);
			    m_FrameServer.Metadata.SelectExtraDrawing(m_FrameServer.Metadata.AutoNumberManager);
			    m_ActiveTool = m_ActiveTool.KeepTool ? m_ActiveTool : m_PointerTool;
			}
			else
			{
			    CreateNewDrawing();
			}
		}
		private void CreateNewDrawing()
		{
		    m_FrameServer.Metadata.Deselect();
			
			// Add a KeyFrame here if it doesn't exist.
			AddKeyframe();
			
			if (m_ActiveTool != ToolManager.Label)
			{
				// Add an instance of a drawing from the active tool to the current keyframe.
				// The drawing is initialized with the current mouse coordinates.
				AbstractDrawing ad = m_ActiveTool.GetNewDrawing(m_DescaledMouse, m_iCurrentPosition, m_FrameServer.Metadata.AverageTimeStampsPerFrame);
				
				m_FrameServer.Metadata[m_iActiveKeyFrameIndex].AddDrawing(ad);
				m_FrameServer.Metadata.SelectedDrawingFrame = m_iActiveKeyFrameIndex;
				m_FrameServer.Metadata.SelectedDrawing = 0;
				
				// Post creation hacks.
				if(ad is DrawingLine2D)
				{
					((DrawingLine2D)ad).ParentMetadata = m_FrameServer.Metadata;
					((DrawingLine2D)ad).ShowMeasure = DrawingToolLine2D.ShowMeasure;
				}
				else if(ad is DrawingCross2D)
				{
					((DrawingCross2D)ad).ParentMetadata = m_FrameServer.Metadata;
					((DrawingCross2D)ad).ShowCoordinates = DrawingToolCross2D.ShowCoordinates;
				}
				else if(ad is DrawingPlane)
				{
				    ((DrawingPlane)ad).SetLocations(m_FrameServer.Metadata.ImageSize, 1.0, Point.Empty);
				}
			}
			else
			{
				// We are using the Text Tool. This is a special case because
				// if we are on an existing Textbox, we just go into edit mode
				// otherwise, we add and setup a new textbox.
				bool bEdit = false;
				foreach (AbstractDrawing ad in m_FrameServer.Metadata[m_iActiveKeyFrameIndex].Drawings)
				{
					if (ad is DrawingText)
					{
						int hitRes = ad.HitTest(m_DescaledMouse, m_iCurrentPosition);
						if (hitRes >= 0)
						{
							bEdit = true;
							((DrawingText)ad).SetEditMode(true, m_FrameServer.CoordinateSystem);
						}
					}
				}
				
				// If we are not on an existing textbox : create new DrawingText.
				if (!bEdit)
				{
					m_FrameServer.Metadata[m_iActiveKeyFrameIndex].AddDrawing(m_ActiveTool.GetNewDrawing(m_DescaledMouse, m_iCurrentPosition, m_FrameServer.Metadata.AverageTimeStampsPerFrame));
					m_FrameServer.Metadata.SelectedDrawingFrame = m_iActiveKeyFrameIndex;
					m_FrameServer.Metadata.SelectedDrawing = 0;
					
					DrawingText dt = (DrawingText)m_FrameServer.Metadata[m_iActiveKeyFrameIndex].Drawings[0];
					
					dt.ContainerScreen = pbSurfaceScreen;
					dt.SetEditMode(true, m_FrameServer.CoordinateSystem);
					panelCenter.Controls.Add(dt.EditBox);
					dt.EditBox.BringToFront();
					dt.EditBox.Focus();
				}
			}
		}
		private void SurfaceScreen_RightDown()
		{
		    // Show the right Pop Menu depending on context.
			// (Drawing, Trajectory, Chronometer, Magnifier, Nothing)
			
			if (m_bIsCurrentlyPlaying)
			{
				mnuDirectTrack.Visible = false;
				mnuSendPic.Visible = false;
				panelCenter.ContextMenuStrip = popMenu;
				return;
			}
			
			m_FrameServer.Metadata.UnselectAll();
			AbstractDrawing hitDrawing = null;
				
			if(InteractiveFiltering)
			{
				mnuDirectTrack.Visible = false;
				mnuSendPic.Visible = false;
				panelCenter.ContextMenuStrip = popMenu;
			}
			else if (m_FrameServer.Metadata.IsOnDrawing(m_iActiveKeyFrameIndex, m_DescaledMouse, m_iCurrentPosition))
			{
				// Rebuild the context menu according to the capabilities of the drawing we are on.
				
				AbstractDrawing ad = m_FrameServer.Metadata.Keyframes[m_FrameServer.Metadata.SelectedDrawingFrame].Drawings[m_FrameServer.Metadata.SelectedDrawing];
				if(ad != null)
				{
					popMenuDrawings.Items.Clear();
					
					// Generic context menu from drawing capabilities.
					if((ad.Caps & DrawingCapabilities.ConfigureColor) == DrawingCapabilities.ConfigureColor)
					{
                        mnuConfigureDrawing.Text = ScreenManagerLang.mnuConfigureDrawing_Color;
                        popMenuDrawings.Items.Add(mnuConfigureDrawing);
					}

					if((ad.Caps & DrawingCapabilities.ConfigureColorSize) == DrawingCapabilities.ConfigureColorSize)
					{
                        mnuConfigureDrawing.Text = ScreenManagerLang.mnuConfigureDrawing_ColorSize;
                        popMenuDrawings.Items.Add(mnuConfigureDrawing);
					}
						
					if(m_PrefManager.DefaultFading.Enabled && ((ad.Caps & DrawingCapabilities.Fading) == DrawingCapabilities.Fading))
					{
						popMenuDrawings.Items.Add(mnuConfigureFading);
					}
					
					if((ad.Caps & DrawingCapabilities.Opacity) == DrawingCapabilities.Opacity)
					{
						popMenuDrawings.Items.Add(mnuConfigureOpacity);
					}
					
					popMenuDrawings.Items.Add(mnuSepDrawing);

					// Specific menus. Hosted by the drawing itself.
					bool hasExtraMenu = (ad.ContextMenu != null && ad.ContextMenu.Count > 0);
					if(hasExtraMenu)
					{
						foreach(ToolStripMenuItem tsmi in ad.ContextMenu)
						{
							tsmi.Tag = (Action)DoInvalidate;	// Inject dependency on this screen's invalidate method.
							popMenuDrawings.Items.Add(tsmi);
						}
					}
					
					bool gotoVisible = (m_PrefManager.DefaultFading.Enabled && (ad.infosFading.ReferenceTimestamp != m_iCurrentPosition));
					if(gotoVisible)
						popMenuDrawings.Items.Add(mnuGotoKeyframe);
					
					if(hasExtraMenu || gotoVisible)
						popMenuDrawings.Items.Add(mnuSepDrawing2);
						
					// Generic delete
					popMenuDrawings.Items.Add(mnuDeleteDrawing);
					
					// Set this menu as the context menu.
					panelCenter.ContextMenuStrip = popMenuDrawings;
				}
			} 
			else if( (hitDrawing = m_FrameServer.Metadata.IsOnExtraDrawing(m_DescaledMouse, m_iCurrentPosition)) != null)
			{ 
				// Unlike attached drawings, each extra drawing type has its own context menu for now.
				
				if(hitDrawing is DrawingChrono)
				{
					// Toggle to countdown is active only if we have a stop time.
					mnuChronoCountdown.Enabled = ((DrawingChrono)hitDrawing).HasTimeStop;
					mnuChronoCountdown.Checked = ((DrawingChrono)hitDrawing).CountDown;
					panelCenter.ContextMenuStrip = popMenuChrono;
				}
				else if(hitDrawing is Track)
				{
					if (((Track)hitDrawing).Status == TrackStatus.Edit)
					{
						mnuStopTracking.Visible = true;
						mnuRestartTracking.Visible = false;
					}
					else
					{
						mnuStopTracking.Visible = false;
						mnuRestartTracking.Visible = true;
					}	
					
					panelCenter.ContextMenuStrip = popMenuTrack;
				}
				else if(hitDrawing is AbstractMultiDrawing)
				{
                    panelCenter.ContextMenuStrip = popMenuMultiDrawing;
				}
			}
			else if (m_FrameServer.Metadata.Magnifier.Mode == MagnifierMode.Indirect && m_FrameServer.Metadata.Magnifier.IsOnObject(m_DescaledMouse))
			{
				panelCenter.ContextMenuStrip = popMenuMagnifier;
			}
			else if(m_ActiveTool != m_PointerTool)
			{
				// Launch FormToolPreset.
				FormToolPresets ftp = new FormToolPresets(m_ActiveTool);
				ScreenManagerKernel.LocateForm(ftp);
				ftp.ShowDialog();
				ftp.Dispose();
				UpdateCursor();
			}
			else
			{
				// No drawing touched and no tool selected, but not currently playing. Default menu.
				mnuDirectTrack.Visible = true;
				mnuSendPic.Visible = m_bSynched;
				panelCenter.ContextMenuStrip = popMenu;
			}
		}
		private void SurfaceScreen_MouseMove(object sender, MouseEventArgs e)
		{
			// We must keep the same Z order.
			// 1:Magnifier, 2:Drawings, 3:Chronos/Tracks
			// When creating a drawing, the active tool will stay on this drawing until its setup is over.
			// After the drawing is created, we either fall back to Pointer tool or stay on the same tool.

			if(!m_FrameServer.Loaded)
                return;

            m_DescaledMouse = m_FrameServer.CoordinateSystem.Untransform(e.Location);
						
			if (e.Button == MouseButtons.None && m_FrameServer.Metadata.Magnifier.Mode == MagnifierMode.Direct)
			{
			    m_FrameServer.Metadata.Magnifier.Move(m_DescaledMouse);
				
				if (!m_bIsCurrentlyPlaying)
					DoInvalidate();
			}
			else if (e.Button == MouseButtons.Left)
			{
				if (m_ActiveTool != m_PointerTool)
				{
				    // Tools that are not IInitializable should reset to Pointer tool after creation.
				    
				    if(m_ActiveTool == ToolManager.Spotlight)
    			    {
    			        IInitializable initializableDrawing = m_FrameServer.Metadata.SpotlightManager as IInitializable;
    			        initializableDrawing.ContinueSetup(m_DescaledMouse, ModifierKeys);
    			    }
					else if (m_iActiveKeyFrameIndex >= 0 && m_FrameServer.Metadata.SelectedDrawing >= 0 && !m_bIsCurrentlyPlaying)
					{
						// Currently setting the second point of a Drawing.
						IInitializable initializableDrawing = m_FrameServer.Metadata[m_iActiveKeyFrameIndex].Drawings[m_FrameServer.Metadata.SelectedDrawing] as IInitializable;
						if(initializableDrawing != null)
							initializableDrawing.ContinueSetup(m_DescaledMouse, ModifierKeys);
					}
				}
				else
				{
					bool bMovingMagnifier = false;
					if (m_FrameServer.Metadata.Magnifier.Mode == MagnifierMode.Indirect)
					{
						bMovingMagnifier = m_FrameServer.Metadata.Magnifier.Move(m_DescaledMouse);
					}
					
					if (!bMovingMagnifier && m_ActiveTool == m_PointerTool)
					{
						if (!m_bIsCurrentlyPlaying)
						{
							// Magnifier is not being moved or is invisible, try drawings through pointer tool.
							// (including chronos, tracks and grids)
							bool bMovingObject = m_PointerTool.OnMouseMove(m_FrameServer.Metadata, m_DescaledMouse, m_FrameServer.CoordinateSystem.Location, ModifierKeys);
							
							if (!bMovingObject)
							{
								// User is not moving anything: move the whole image.
								// This may not have any effect if we try to move outside the original size and not in "free move" mode.
								
								// Get mouse deltas (descaled=in image coords).
								double fDeltaX = (double)m_PointerTool.MouseDelta.X;
								double fDeltaY = (double)m_PointerTool.MouseDelta.Y;
								
								if(m_FrameServer.Metadata.Mirrored)
								{
									fDeltaX = -fDeltaX;
								}
								
								m_FrameServer.CoordinateSystem.MoveZoomWindow(fDeltaX, fDeltaY);
							}
						}
					}
				}
				
				if (!m_bIsCurrentlyPlaying)
				{
					DoInvalidate();
				}
            }
		}
		private void SurfaceScreen_MouseUp(object sender, MouseEventArgs e)
		{
			// End of an action.
			// Depending on the active tool we have various things to do.
			
			if(!m_FrameServer.Loaded || e.Button != MouseButtons.Left)
                return;
            
			m_DescaledMouse = m_FrameServer.CoordinateSystem.Untransform(e.Location);
			
			if (m_ActiveTool == m_PointerTool)
			{
				OnPoke();
				m_FrameServer.Metadata.UpdateTrackPoint(m_FrameServer.CurrentImage);
				ReportForSyncMerge();
			}
			
			m_FrameServer.Metadata.Magnifier.OnMouseUp(m_DescaledMouse);
			
			// Memorize the action we just finished to enable undo.
			if(m_ActiveTool == ToolManager.Chrono)
			{
				IUndoableCommand cac = new CommandAddChrono(DoInvalidate, DoDrawingUndrawn, m_FrameServer.Metadata);
				CommandManager cm = CommandManager.Instance();
				cm.LaunchUndoableCommand(cac);
			}
			else if (m_ActiveTool != m_PointerTool)
			{
			    if(m_FrameServer.Metadata.SelectedExtraDrawing >= 0 && m_FrameServer.Metadata.ExtraDrawings[m_FrameServer.Metadata.SelectedExtraDrawing] is AbstractMultiDrawing)
			    {
			        AbstractMultiDrawing extraDrawing = m_FrameServer.Metadata.ExtraDrawings[m_FrameServer.Metadata.SelectedExtraDrawing] as AbstractMultiDrawing;
			        
			        IUndoableCommand cad = new CommandAddMultiDrawingItem(DoInvalidate, DoDrawingUndrawn, m_FrameServer.Metadata);
    				CommandManager cm = CommandManager.Instance();
    				cm.LaunchUndoableCommand(cad);
    				
    				m_FrameServer.Metadata.Deselect();
			    }
			    else if(m_iActiveKeyFrameIndex >= 0)
			    {
        			if (m_bTextEdit)
        			{
        			    m_bTextEdit = false;
        			}
        			else if(m_FrameServer.Metadata.SelectedDrawingFrame >= 0 && m_FrameServer.Metadata.SelectedDrawing >= 0)
        		    {
        				IUndoableCommand cad = new CommandAddDrawing(DoInvalidate, DoDrawingUndrawn, m_FrameServer.Metadata, m_FrameServer.Metadata[m_iActiveKeyFrameIndex].Position);
        				CommandManager cm = CommandManager.Instance();
        				cm.LaunchUndoableCommand(cad);
        				
        				// Deselect the drawing we just added.
        				m_FrameServer.Metadata.Deselect();
        		    }
			    }
			}
			
			// The fact that we stay on this tool or fall back to pointer tool, depends on the tool.
			m_ActiveTool = m_ActiveTool.KeepTool ? m_ActiveTool : m_PointerTool;
			
			if (m_ActiveTool == m_PointerTool)
			{
				SetCursor(m_PointerTool.GetCursor(0));
				m_PointerTool.OnMouseUp();
				
				// If we were resizing an SVG drawing, trigger a render.
				// TODO: this is currently triggered on every mouse up, not only on resize !
				int selectedFrame = m_FrameServer.Metadata.SelectedDrawingFrame;
				int selectedDrawing = m_FrameServer.Metadata.SelectedDrawing;
				if(selectedFrame != -1 && selectedDrawing  != -1)
				{
					DrawingSVG d = m_FrameServer.Metadata.Keyframes[selectedFrame].Drawings[selectedDrawing] as DrawingSVG;
					if(d != null)
						d.ResizeFinished();
				}
			}
			
			if (m_FrameServer.Metadata.SelectedDrawingFrame != -1 && m_FrameServer.Metadata.SelectedDrawing != -1)
			{
				m_DeselectionTimer.Start();					
			}
			
			DoInvalidate();
		}
		private void SurfaceScreen_MouseDoubleClick(object sender, MouseEventArgs e)
		{
			if(!m_FrameServer.Loaded || e.Button != MouseButtons.Left || m_ActiveTool != m_PointerTool)
                return;
                
			OnPoke();
			
			m_DescaledMouse = m_FrameServer.CoordinateSystem.Untransform(e.Location);
			m_FrameServer.Metadata.AllDrawingTextToNormalMode();
			m_FrameServer.Metadata.UnselectAll();
			
			AbstractDrawing hitDrawing = null;
			
			//------------------------------------------------------------------------------------
			// - If on text, switch to edit mode.
			// - If on other drawing, launch the configuration dialog.
			// - Otherwise -> Maximize/Reduce image.
			//------------------------------------------------------------------------------------
			if(InteractiveFiltering)
			{
				ToggleImageFillMode();
			}
			else if (m_FrameServer.Metadata.IsOnDrawing(m_iActiveKeyFrameIndex, m_DescaledMouse, m_iCurrentPosition))
			{
				// Double click on a drawing:
				// turn text tool into edit mode, launch config for others, SVG don't have a config.
				AbstractDrawing ad = m_FrameServer.Metadata.Keyframes[m_FrameServer.Metadata.SelectedDrawingFrame].Drawings[m_FrameServer.Metadata.SelectedDrawing];
				if (ad is DrawingText)
				{
					((DrawingText)ad).SetEditMode(true, m_FrameServer.CoordinateSystem);
					m_ActiveTool = ToolManager.Label;
					m_bTextEdit = true;
				}
				else if(ad is DrawingSVG || ad is DrawingBitmap)
				{
					mnuConfigureOpacity_Click(null, EventArgs.Empty);
				}
				else
				{
					mnuConfigureDrawing_Click(null, EventArgs.Empty);
				}
			}
			else if((hitDrawing = m_FrameServer.Metadata.IsOnExtraDrawing(m_DescaledMouse, m_iCurrentPosition)) != null)
			{
				if(hitDrawing is DrawingChrono)
				{
					mnuChronoConfigure_Click(null, EventArgs.Empty);	
				}
				else if(hitDrawing is Track)
				{
					mnuConfigureTrajectory_Click(null, EventArgs.Empty);	
				}
			}
			else
			{
				ToggleImageFillMode();
			}
		}
		private void SurfaceScreen_Paint(object sender, PaintEventArgs e)
		{
			//-------------------------------------------------------------------
			// We always draw at full SurfaceScreen size.
			// It is the SurfaceScreen itself that is resized if needed.
			//-------------------------------------------------------------------
			if(!m_FrameServer.Loaded || m_DualSaveInProgress)
                return;
            
			m_TimeWatcher.LogTime("Actual start of paint");
			
			if(InteractiveFiltering)
			{
			    m_InteractiveEffect.Draw(e.Graphics, m_FrameServer.VideoReader.WorkingZoneFrames);
			}
			else if(m_FrameServer.CurrentImage != null)
			{
				try
				{
				    // If we are on a keyframe, see if it has any drawing.
					int iKeyFrameIndex = -1;
					if (m_iActiveKeyFrameIndex >= 0)
					{
						if (m_FrameServer.Metadata[m_iActiveKeyFrameIndex].Drawings.Count > 0)
						{
							iKeyFrameIndex = m_iActiveKeyFrameIndex;
						}
					}
					
					FlushOnGraphics(m_FrameServer.CurrentImage, e.Graphics, m_viewportManipulator.RenderingSize, iKeyFrameIndex, m_iCurrentPosition);
					
					if(m_MessageToaster.Enabled)
						m_MessageToaster.Draw(e.Graphics);
                   
        			//log.DebugFormat("play loop to end of paint: {0}/{1}", m_Stopwatch.ElapsedMilliseconds, m_FrameServer.VideoReader.Info.FrameIntervalMilliseconds);
				}
				catch (System.InvalidOperationException)
				{
					log.Error("Error while painting image. Object is currently in use elsewhere... ATI Drivers ?");
				}
				catch (Exception exp)
				{
					log.Error("Error while painting image.");
					log.Error(exp.Message);
					log.Error(exp.StackTrace);
				    
					#if DEBUG
				    throw new Exception();
					#endif
				}
			}
			else
			{
				log.Error("Painting screen - no image to display.");
			}
			
			// Draw Selection Border if needed.
			if (m_bShowImageBorder)
			{
				DrawImageBorder(e.Graphics);
			}
			
			m_TimeWatcher.LogTime("Finished painting.");
		}
		private void SurfaceScreen_MouseEnter(object sender, EventArgs e)
		{
			// Set focus to surfacescreen to enable mouse scroll
			
			// But only if there is no Text edition going on.
			bool bEditing = false;
			if(m_FrameServer.Metadata.Count > m_iActiveKeyFrameIndex && m_iActiveKeyFrameIndex >= 0)
			{
				foreach (AbstractDrawing ad in m_FrameServer.Metadata[m_iActiveKeyFrameIndex].Drawings)
				{
					DrawingText dt = ad as DrawingText;
					if (dt != null)
					{
						if(dt.EditMode)
						{
							bEditing = true;
							break;
						}
					}
				}
			}
			
			if(!bEditing)
			{
				pbSurfaceScreen.Focus();
			}
			
		}
		private void FlushOnGraphics(Bitmap _sourceImage, Graphics g, Size _renderingSize, int _iKeyFrameIndex, long _iPosition)
		{
			// This function is used both by the main rendering loop and by image export functions.
			// Video export get its image from the VideoReader or the cache.

			// Notes on performances:
			// - The global performance depends on the size of the *source* image. Not destination.
			//   (rendering 1 pixel from an HD source will still be slow)
			// - Using a matrix transform instead of the buit in interpolation doesn't seem to do much.
			// - InterpolationMode has a sensible effect. but can look ugly for lowest values.
			// - Using unmanaged BitBlt or StretchBlt doesn't seem to do much... (!?)
			// - the scaling and interpolation better be done directly from ffmpeg. (cut on memory usage too)
			// - furthermore ffmpeg has a mode called 'FastBilinear' that seems more promising.
			// - Drawing unscaled avoid the interpolation altogether and provide ~8x perfs.
			
			// 1. Image
			g.PixelOffsetMode = PixelOffsetMode.HighSpeed;
			//g.CompositingQuality = CompositingQuality.HighSpeed;
			//g.InterpolationMode = InterpolationMode.Bilinear;
			//g.InterpolationMode = InterpolationMode.NearestNeighbor;
			//g.SmoothingMode = SmoothingMode.None;
			
			m_TimeWatcher.LogTime("Before DrawImage");
			
			if(m_viewportManipulator.MayDrawUnscaled && m_FrameServer.VideoReader.CanDrawUnscaled)
			{
			    // Source image should be at the right size, unless it has been temporarily disabled.
			    if(m_FrameServer.CoordinateSystem.RenderingZoomWindow.Size.CloseTo(_renderingSize) && !m_FrameServer.Metadata.Mirrored)
			    {
			        if(!m_FrameServer.CoordinateSystem.Zooming)
    			    {
                        g.DrawImageUnscaled(_sourceImage, 0, 0);
                        //log.DebugFormat("draw unscaled.");
    			    }
    			    else
    			    {
    			        int left = - m_FrameServer.CoordinateSystem.RenderingZoomWindow.Left;
                        int top = - m_FrameServer.CoordinateSystem.RenderingZoomWindow.Top;
                        g.DrawImageUnscaled(_sourceImage, left, top);
                        //log.DebugFormat("draw unscaled with zoom.");
    			    }
			    }
			    else
			    {
			        // Image was decoded at customized size, but can't be rendered unscaled.
                    Rectangle rDst;
                    if(m_FrameServer.Metadata.Mirrored)
                        rDst = new Rectangle(_renderingSize.Width, 0, -_renderingSize.Width, _renderingSize.Height);
                    else
                        rDst = new Rectangle(0, 0, _renderingSize.Width, _renderingSize.Height);
			        
                    // TODO: integrate the mirror flag into the CoordinateSystem.
                    
			        g.DrawImage(_sourceImage, rDst, m_FrameServer.CoordinateSystem.RenderingZoomWindow, GraphicsUnit.Pixel);
			        //log.DebugFormat("draw scaled at custom decoding size.");
			    }
			}
			else
			{
			    if(!m_FrameServer.CoordinateSystem.Zooming && !m_FrameServer.Metadata.Mirrored && m_FrameServer.CoordinateSystem.Stretch == 1.0f)
			    {
			        // This allow to draw unscaled while tracking or caching for example, provided we are rendering at original size.
			        g.DrawImageUnscaled(_sourceImage, 0, 0);
                    //log.DebugFormat("drawing unscaled because at the right size.");
			    }
			    else
			    {
                    Rectangle rDst;
                    if(m_FrameServer.Metadata.Mirrored)
                    rDst = new Rectangle(_renderingSize.Width, 0, -_renderingSize.Width, _renderingSize.Height);
                    else
                    rDst = new Rectangle(0, 0, _renderingSize.Width, _renderingSize.Height);
                    
                    g.DrawImage(_sourceImage, rDst, m_FrameServer.CoordinateSystem.ZoomWindow, GraphicsUnit.Pixel);
                    //log.DebugFormat("drawing scaled.");
			    }
			}
			
			m_TimeWatcher.LogTime("After DrawImage");
            
			// Testing Key images overlay.
			// Creates a ghost image of the last keyframe superposed with the current image.
			// We can only do it in analysis mode to get the key image bitmap.
			/*if(m_FrameServer.VideoFile.Selection.iAnalysisMode == 1 && m_FrameServer.Metadata.Keyframes.Count > 0)
			{
				// Look for the closest key image before.
				int iImageMerge = -1 ;
				long iBestDistance = long.MaxValue;	
				for(int i=0; i<m_FrameServer.Metadata.Keyframes.Count;i++)
				{
					long iDistance = _iPosition - m_FrameServer.Metadata.Keyframes[i].Position;
					if(iDistance >=0 && iDistance < iBestDistance)
					{
						iBestDistance = iDistance;
						iImageMerge = i;
					}
				}
				
				// Merge images.
				int iFrameIndex = (int)m_FrameServer.VideoFile.GetFrameNumber(m_FrameServer.Metadata.Keyframes[iImageMerge].Position);
				Bitmap mergeImage = m_FrameServer.VideoFile.FrameList[iFrameIndex].BmpImage;
				g.DrawImage(mergeImage, rDst, 0, 0, _sourceImage.Width, _sourceImage.Height, GraphicsUnit.Pixel, m_SyncMergeImgAttr);
			}*/
			
			// .Sync superposition.
			if(m_bSynched && m_bSyncMerge && m_SyncMergeImage != null)
			{
				// The mirroring, if any, will have been done already and applied to the sync image.
				// (because to draw the other image, we take into account its own mirroring option,
				// not the option in this screen.)
				Rectangle rSyncDst = new Rectangle(0, 0, _renderingSize.Width, _renderingSize.Height);
                g.DrawImage(m_SyncMergeImage, rSyncDst, 0, 0, m_SyncMergeImage.Width, m_SyncMergeImage.Height, GraphicsUnit.Pixel, m_SyncMergeImgAttr);
			}
			
			if ((m_bIsCurrentlyPlaying && m_PrefManager.DrawOnPlay) || !m_bIsCurrentlyPlaying)
			{
                FlushDrawingsOnGraphics(g, m_FrameServer.CoordinateSystem, _iKeyFrameIndex, _iPosition);
				FlushMagnifierOnGraphics(_sourceImage, g, m_FrameServer.CoordinateSystem);
			}
		}
		private void FlushDrawingsOnGraphics(Graphics _canvas, CoordinateSystem _transformer, int _iKeyFrameIndex, long _iPosition)
		{
			// Prepare for drawings
			_canvas.SmoothingMode = SmoothingMode.AntiAlias;
            _canvas.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;

			// 1. Extra (non attached to any key image).
			for (int i = 0; i < m_FrameServer.Metadata.ExtraDrawings.Count; i++)
			{
			    bool selected = (i == m_FrameServer.Metadata.SelectedExtraDrawing);
				m_FrameServer.Metadata.ExtraDrawings[i].Draw(_canvas, _transformer, selected, _iPosition);
			}
			
			// 2. Drawings attached to key images.
			if (m_PrefManager.DefaultFading.Enabled)
			{
				// If fading is on, we ask all drawings to draw themselves with their respective
				// fading factor for this position.

				int[] zOrder = m_FrameServer.Metadata.GetKeyframesZOrder(_iPosition);

				// Draw in reverse keyframes z order so the closest next keyframe gets drawn on top (last).
				for (int ikf = zOrder.Length-1; ikf >= 0 ; ikf--)
				{
					Keyframe kf = m_FrameServer.Metadata.Keyframes[zOrder[ikf]];
					for (int idr = kf.Drawings.Count - 1; idr >= 0; idr--)
					{
						bool bSelected = (zOrder[ikf] == m_FrameServer.Metadata.SelectedDrawingFrame && idr == m_FrameServer.Metadata.SelectedDrawing);
                        kf.Drawings[idr].Draw(_canvas, _transformer, bSelected, _iPosition);
					}
				}
			}
			else if (_iKeyFrameIndex >= 0)
			{
				// if fading is off, only draw the current keyframe.
				// Draw all drawings in reverse order to get first object on the top of Z-order.
				for (int i = m_FrameServer.Metadata[_iKeyFrameIndex].Drawings.Count - 1; i >= 0; i--)
				{
					bool bSelected = (_iKeyFrameIndex == m_FrameServer.Metadata.SelectedDrawingFrame && i == m_FrameServer.Metadata.SelectedDrawing);
                    m_FrameServer.Metadata[_iKeyFrameIndex].Drawings[i].Draw(_canvas, _transformer, bSelected, _iPosition);
				}
			}
			else
			{
				// This is not a Keyframe, and fading is off.
				// Hence, there is no drawings to draw here.
			}
		}
		private void FlushMagnifierOnGraphics(Bitmap _sourceImage, Graphics g, CoordinateSystem _transformer)
		{
			// Note: the Graphics object must not be the one extracted from the image itself.
			// If needed, clone the image.
			if (_sourceImage != null && m_FrameServer.Metadata.Magnifier.Mode != MagnifierMode.None)
				m_FrameServer.Metadata.Magnifier.Draw(_sourceImage, g, _transformer, m_FrameServer.Metadata.Mirrored, m_FrameServer.VideoReader.Info.AspectRatioSize);
		}
		private void DoInvalidate()
		{
			// This function should be the single point where we call for rendering.
			// Here we can decide to render directly on the surface, go through the Windows message pump, force the refresh, etc.
			
			// Invalidate is asynchronous and several Invalidate calls will be grouped together. (Only one repaint will be done).
			pbSurfaceScreen.Invalidate();
		}
		#endregion

		#region PanelCenter Events
		private void PanelCenter_MouseEnter(object sender, EventArgs e)
		{
			// Give focus to enable mouse scroll.
			panelCenter.Focus();
		}
		private void PanelCenter_MouseClick(object sender, MouseEventArgs e)
		{
			OnPoke();
		}
		private void PanelCenter_Resize(object sender, EventArgs e)
		{
		    if(m_Constructed)
                ResizeUpdate(true);
		}
		private void PanelCenter_MouseDown(object sender, MouseEventArgs e)
		{
			mnuDirectTrack.Visible = false;
			mnuSendPic.Visible = m_bSynched;
			panelCenter.ContextMenuStrip = popMenu;
		}
		#endregion
		
		#region Keyframes Panel
		private void pnlThumbnails_MouseEnter(object sender, EventArgs e)
		{
			// Give focus to disable keyframe box editing.
			pnlThumbnails.Focus();
		}
		private void splitKeyframes_Resize(object sender, EventArgs e)
		{
			// Redo the dock/undock if needed to be at the right place.
			// (Could be handled by layout ?)
			DockKeyframePanel(m_bDocked);
		}
		private void btnAddKeyframe_Click(object sender, EventArgs e)
		{
			if (m_FrameServer.Loaded)
			{
				AddKeyframe();

				// Set as active screen is done afterwards, so the export as pdf menu is activated
				// even if we had no keyframes yet.
				OnPoke();
			}
		}
		private void OrganizeKeyframes()
		{
			// Should only be called when adding/removing a Thumbnail
			
			pnlThumbnails.Controls.Clear();

			if (m_FrameServer.Metadata.Count > 0)
			{
				int iKeyframeIndex = 0;
				int iPixelsOffset = 0;
				int iPixelsSpacing = 20;

				foreach (Keyframe kf in m_FrameServer.Metadata.Keyframes)
				{
					KeyframeBox box = new KeyframeBox(kf);
					SetupDefaultThumbBox(box);
					
					// Finish the setup
					box.Left = iPixelsOffset + iPixelsSpacing;

					box.UpdateTitle(kf.Title);
					box.Tag = iKeyframeIndex;
					box.pbThumbnail.SizeMode = PictureBoxSizeMode.StretchImage;
					
					box.CloseThumb += ThumbBoxClose;
					box.ClickThumb += ThumbBoxClick;
					box.ClickInfos += ThumbBoxInfosClick;
					
					// TODO - Titre de la Keyframe en ToolTip.
					iPixelsOffset += (iPixelsSpacing + box.Width);

					pnlThumbnails.Controls.Add(box);

					iKeyframeIndex++;
				}
				
				EnableDisableKeyframes();
				pnlThumbnails.Refresh();
			}
			else
			{
				DockKeyframePanel(true);
				m_iActiveKeyFrameIndex = -1;
			}
			
			UpdateFramesMarkers();
			DoInvalidate(); // Because of trajectories with keyframes labels.
		}
		private void SetupDefaultThumbBox(UserControl _box)
		{
			_box.Top = 10;
			_box.Cursor = Cursors.Hand;
		}
		private void ActivateKeyframe(long _iPosition)
		{
			ActivateKeyframe(_iPosition, true);
		}
		private void ActivateKeyframe(long _iPosition, bool _bAllowUIUpdate)
		{
			//--------------------------------------------------------------
			// Black border every keyframe, unless it is at the given position.
			// This method might be called with -1 to force complete blackout.
			//--------------------------------------------------------------

			// This method is called on each frame during frametracker browsing
			// keep it fast or fix the strategy.

			m_iActiveKeyFrameIndex = -1;

			// We leverage the fact that pnlThumbnail is exclusively populated with thumboxes.
			for (int i = 0; i < pnlThumbnails.Controls.Count; i++)
			{
				if (m_FrameServer.Metadata[i].Position == _iPosition)
				{
					m_iActiveKeyFrameIndex = i;
					if(_bAllowUIUpdate)
						((KeyframeBox)pnlThumbnails.Controls[i]).DisplayAsSelected(true);

					// Make sure the thumbnail is always in the visible area by auto scrolling.
					if(_bAllowUIUpdate) pnlThumbnails.ScrollControlIntoView(pnlThumbnails.Controls[i]);
				}
				else
				{
					if(_bAllowUIUpdate)
						((KeyframeBox)pnlThumbnails.Controls[i]).DisplayAsSelected(false);
				}
			}

			if (_bAllowUIUpdate && m_KeyframeCommentsHub.UserActivated && m_iActiveKeyFrameIndex >= 0)
			{
				m_KeyframeCommentsHub.UpdateContent(m_FrameServer.Metadata[m_iActiveKeyFrameIndex]);
				m_KeyframeCommentsHub.Visible = true;
			}
			else
			{
			    if(m_KeyframeCommentsHub.Visible)
                    m_KeyframeCommentsHub.CommitChanges();
				
			    m_KeyframeCommentsHub.Visible = false;
			}
		}
		private void EnableDisableKeyframes()
		{
			// Enable Keyframes that are within Working Zone, Disable others.

			// We leverage the fact that pnlThumbnail is exclusively populated with thumboxes.
			for (int i = 0; i < pnlThumbnails.Controls.Count; i++)
			{
				KeyframeBox tb = pnlThumbnails.Controls[i] as KeyframeBox;
				if(tb != null)
				{
					m_FrameServer.Metadata[i].TimeCode = TimeStampsToTimecode(m_FrameServer.Metadata[i].Position - m_iSelStart, m_PrefManager.TimeCodeFormat, false);
					
					// Enable thumbs that are within Working Zone, grey out others.
					if (m_FrameServer.Metadata[i].Position >= m_iSelStart && m_FrameServer.Metadata[i].Position <= m_iSelEnd)
					{
						m_FrameServer.Metadata[i].Disabled = false;
						
						tb.Enabled = true;
						tb.pbThumbnail.Image = m_FrameServer.Metadata[i].Thumbnail;
					}
					else
					{
						m_FrameServer.Metadata[i].Disabled = true;
						
						tb.Enabled = false;
						tb.pbThumbnail.Image = m_FrameServer.Metadata[i].DisabledThumbnail;
					}

					tb.UpdateTitle(m_FrameServer.Metadata[i].Title);
				}
			}
		}
		public void OnKeyframesTitleChanged()
		{
			// Update trajectories.
			m_FrameServer.Metadata.UpdateTrajectoriesForKeyframes();
			// Update thumb boxes.
			EnableDisableKeyframes();
			DoInvalidate();
		}
		private void GotoNextKeyframe()
		{
			if (m_FrameServer.Metadata.Count > 1)
			{
				int iNextKeyframe = -1;
				for (int i = 0; i < m_FrameServer.Metadata.Count; i++)
				{
					if (m_iCurrentPosition < m_FrameServer.Metadata[i].Position)
					{
						iNextKeyframe = i;
						break;
					}
				}

				if (iNextKeyframe >= 0 && m_FrameServer.Metadata[iNextKeyframe].Position <= m_iSelEnd)
				{
					ThumbBoxClick(pnlThumbnails.Controls[iNextKeyframe], EventArgs.Empty);
				}
				
			}
		}
		private void GotoPreviousKeyframe()
		{
			if (m_FrameServer.Metadata.Count > 0)
			{
				int iPrevKeyframe = -1;
				for (int i = m_FrameServer.Metadata.Count - 1; i >= 0; i--)
				{
					if (m_iCurrentPosition > m_FrameServer.Metadata[i].Position)
					{
						iPrevKeyframe = i;
						break;
					}
				}

				if (iPrevKeyframe >= 0 && m_FrameServer.Metadata[iPrevKeyframe].Position >= m_iSelStart)
				{
					ThumbBoxClick(pnlThumbnails.Controls[iPrevKeyframe], EventArgs.Empty);
				}

			}
		}

		private void AddKeyframe()
		{
			int i = 0;
			// Check if it's not already registered.
			bool bAlreadyKeyFrame = false;
			for (i = 0; i < m_FrameServer.Metadata.Count; i++)
			{
				if (m_FrameServer.Metadata[i].Position == m_iCurrentPosition)
				{
					bAlreadyKeyFrame = true;
					m_iActiveKeyFrameIndex = i;
				}
			}

			// Add it to the list.
			if (!bAlreadyKeyFrame)
			{
				IUndoableCommand cak = new CommandAddKeyframe(this, m_FrameServer.Metadata, m_iCurrentPosition);
				CommandManager cm = CommandManager.Instance();
				cm.LaunchUndoableCommand(cak);
				
				// If it is the very first key frame, we raise the KF panel.
				// Otherwise we keep whatever choice the user made.
				if(m_FrameServer.Metadata.Count == 1)
				{
					DockKeyframePanel(false);
				}
			}
		}
		public void OnAddKeyframe(long _iPosition)
		{
			// Public because called from CommandAddKeyframe.Execute()
			// Title becomes the current timecode. (relative to sel start or sel minimum ?)
			
			Keyframe kf = new Keyframe(_iPosition, TimeStampsToTimecode(_iPosition - m_iSelStart, m_PrefManager.TimeCodeFormat, m_bSynched), m_FrameServer.CurrentImage, m_FrameServer.Metadata);
			
			if (_iPosition != m_iCurrentPosition)
			{
				// Move to the required Keyframe.
				// Should only happen when Undoing a DeleteKeyframe.
				m_iFramesToDecode = 1;
				ShowNextFrame(_iPosition, true);
				UpdatePositionUI();

				// Readjust and complete the Keyframe
				kf.ImportImage(m_FrameServer.CurrentImage);
			}

			m_FrameServer.Metadata.Add(kf);

			// Keep the list sorted
			m_FrameServer.Metadata.Sort();
			m_FrameServer.Metadata.UpdateTrajectoriesForKeyframes();

			// Refresh Keyframes preview.
			OrganizeKeyframes();

			// B&W conversion can be lengthly. We do it after showing the result.
			kf.GenerateDisabledThumbnail();

			if (!m_bIsCurrentlyPlaying)
			{
				ActivateKeyframe(m_iCurrentPosition);
			}
			
		}
		private void RemoveKeyframe(int _iKeyframeIndex)
		{
			IUndoableCommand cdk = new CommandDeleteKeyframe(this, m_FrameServer.Metadata, m_FrameServer.Metadata[_iKeyframeIndex].Position);
			CommandManager cm = CommandManager.Instance();
			cm.LaunchUndoableCommand(cdk);

			//OnRemoveKeyframe(_iKeyframeIndex);
		}
		public void OnRemoveKeyframe(int _iKeyframeIndex)
		{
			if (_iKeyframeIndex == m_iActiveKeyFrameIndex)
			{
				// Removing active frame
				m_iActiveKeyFrameIndex = -1;
			}
			else if (_iKeyframeIndex < m_iActiveKeyFrameIndex)
			{
				if (m_iActiveKeyFrameIndex > 0)
				{
					// Active keyframe index shift
					m_iActiveKeyFrameIndex--;
				}
			}

			m_FrameServer.Metadata.RemoveAt(_iKeyframeIndex);
			m_FrameServer.Metadata.UpdateTrajectoriesForKeyframes();
			OrganizeKeyframes();
			DoInvalidate();
		}
		public void UpdateKeyframes()
		{
			// Primary selection has been image-adjusted,
			// some keyframes may have been impacted.

			bool bAtLeastOne = false;

			foreach (Keyframe kf in m_FrameServer.Metadata.Keyframes)
			{
				if (kf.Position >= m_iSelStart && kf.Position <= m_iSelEnd)
				{
//kf.ImportImage(m_FrameServer.VideoReader.FrameList[(int)m_FrameServer.VideoReader.GetFrameNumber(kf.Position)].BmpImage);
					kf.GenerateDisabledThumbnail();
					bAtLeastOne = true;
				}
				else
				{
					// Outside selection : couldn't possibly be impacted.
				}
			}

			if (bAtLeastOne)
				OrganizeKeyframes();

		}
		private void pnlThumbnails_DoubleClick(object sender, EventArgs e)
		{
			if (m_FrameServer.Loaded)
			{
				// On double click in the thumbs panel : Add a keyframe at current pos.
				AddKeyframe();
				OnPoke();
			}
		}

		#region ThumbBox event Handlers
		private void ThumbBoxClose(object sender, EventArgs e)
		{
			RemoveKeyframe((int)((KeyframeBox)sender).Tag);

			// Set as active screen is done after in case we don't have any keyframes left.
			OnPoke();
		}
		private void ThumbBoxClick(object sender, EventArgs e)
		{
			// Move to the right spot.
			OnPoke();
			StopPlaying();
			m_PlayerScreenUIHandler.PlayerScreenUI_PauseAsked();

			long iTargetPosition = m_FrameServer.Metadata[(int)((KeyframeBox)sender).Tag].Position;

			trkSelection.SelPos = iTargetPosition;
			m_iFramesToDecode = 1;


			ShowNextFrame(iTargetPosition, true);
			m_iCurrentPosition = iTargetPosition;

			UpdatePositionUI();

			// On active sur la position r�elle, au cas o� on ne soit pas sur la frame demand�e.
			// par ex, si la kf cliqu�e est hors zone
			ActivateKeyframe(m_iCurrentPosition);
		}
		private void ThumbBoxInfosClick(object sender, EventArgs e)
		{
			ThumbBoxClick(sender, e);
			m_KeyframeCommentsHub.UserActivated = true;
			ActivateKeyframe(m_iCurrentPosition);
		}
		#endregion

		#region Docking Undocking
		private void btnDockBottom_Click(object sender, EventArgs e)
		{
			DockKeyframePanel(!m_bDocked);
		}
		private void splitKeyframes_Panel2_DoubleClick(object sender, EventArgs e)
		{
			DockKeyframePanel(!m_bDocked);
		}
		private void DockKeyframePanel(bool _bDock)
		{
			if(_bDock)
			{
				// hide the keyframes, change image.
				splitKeyframes.SplitterDistance = splitKeyframes.Height - 25;
				btnDockBottom.BackgroundImage = Resources.undock16x16;
				btnDockBottom.Visible = m_FrameServer.Metadata.Count > 0;
			}
			else
			{
				// show the keyframes, change image.
				splitKeyframes.SplitterDistance = splitKeyframes.Height - 140;
				btnDockBottom.BackgroundImage = Resources.dock16x16;
				btnDockBottom.Visible = true;
			}
			
			m_bDocked = _bDock;
		}
		private void PrepareKeyframesDock()
		{
			// If there's no keyframe, and we will be using a tool,
			// the keyframes dock should be raised.
			// This way we don't surprise the user when he click the screen and the image moves around.
			// (especially problematic when using the Pencil.
			
			// this is only done for the very first keyframe.
			if (m_FrameServer.Metadata.Count < 1)
			{
				DockKeyframePanel(false);
			}
		}
		#endregion

		#endregion

		#region Drawings Toolbar Events
		private void drawingTool_Click(object sender, EventArgs e)
		{
			// User clicked on a drawing tool button. A reference to the tool is stored in .Tag
			// Set this tool as the active tool (waiting for the actual use) and set the cursor accordingly.
			
			// Deactivate magnifier if not commited.
			if(m_FrameServer.Metadata.Magnifier.Mode == MagnifierMode.Direct)
			{
				DisableMagnifier();
			}
			
			OnPoke();
			
			AbstractDrawingTool tool = ((ToolStripItem)sender).Tag as AbstractDrawingTool;
    		m_ActiveTool = tool ?? m_PointerTool;
			
			UpdateCursor();
			
			// Ensure there's a key image at this position, unless the tool creates unattached drawings.
			if(m_ActiveTool == m_PointerTool && m_FrameServer.Metadata.Count < 1)
				DockKeyframePanel(true);
			else if(m_ActiveTool.Attached)
				PrepareKeyframesDock();
			
			pbSurfaceScreen.Invalidate();
		}
		private void btnMagnifier_Click(object sender, EventArgs e)
		{
			if (!m_FrameServer.Loaded)
			    return;
			
			m_ActiveTool = m_PointerTool;

			if (m_FrameServer.Metadata.Magnifier.Mode == MagnifierMode.None)
			{
				UnzoomDirectZoom(false);
				m_FrameServer.Metadata.Magnifier.Mode = MagnifierMode.Direct;
				SetCursor(Cursors.Cross);
			}
			else if (m_FrameServer.Metadata.Magnifier.Mode == MagnifierMode.Direct)
			{
				// Revert to no magnification.
				UnzoomDirectZoom(false);
				m_FrameServer.Metadata.Magnifier.Mode = MagnifierMode.None;
				//btnMagnifier.Image = Drawings.magnifier;
				SetCursor(m_PointerTool.GetCursor(0));
				DoInvalidate();
			}
			else
			{
				DisableMagnifier();
				DoInvalidate();
			}
		}
		private void btnShowComments_Click(object sender, EventArgs e)
		{
			OnPoke();

			if (m_FrameServer.Loaded)
			{
				// If the video is currently playing, the comments are not visible.
				// We stop the video and show them.
				bool bWasPlaying = m_bIsCurrentlyPlaying;
				if (m_bIsCurrentlyPlaying)
				{
					StopPlaying();
					m_PlayerScreenUIHandler.PlayerScreenUI_PauseAsked();
					ActivateKeyframe(m_iCurrentPosition);
				}
				
				if(m_iActiveKeyFrameIndex < 0 || !m_KeyframeCommentsHub.UserActivated || bWasPlaying)
				{
					// As of now, Keyframes infobox should display when we are on a keyframe
					m_KeyframeCommentsHub.UserActivated = true;
					
					if (m_iActiveKeyFrameIndex < 0)
					{
						// We are not on a keyframe but user asked to show the infos...
						// did he want to create a keyframe here and put some infos,
						// or did he only want to activate the infobox for next keyframes ?
						//
						// Since he clicked on the DrawingTools bar, we will act as if it was a Drawing,
						// and add a keyframe here in case there isn't already one.
						AddKeyframe();
					}

					m_KeyframeCommentsHub.UpdateContent(m_FrameServer.Metadata[m_iActiveKeyFrameIndex]);
					m_KeyframeCommentsHub.Visible = true;
				}
				else
				{
					m_KeyframeCommentsHub.UserActivated = false;
					m_KeyframeCommentsHub.CommitChanges();
					m_KeyframeCommentsHub.Visible = false;
				}
				
			}
		}
		private void btnColorProfile_Click(object sender, EventArgs e)
		{
			OnPoke();

			// Load, save or modify current profile.
			FormToolPresets ftp = new FormToolPresets();
			ScreenManagerKernel.LocateForm(ftp);
			ftp.ShowDialog();
			ftp.Dispose();

			UpdateCursor();
			DoInvalidate();
		}
		private void UpdateCursor()
		{
			if(m_ActiveTool == m_PointerTool)
			{
				SetCursor(m_PointerTool.GetCursor(0));
			}
			else
			{
				SetCursor(m_ActiveTool.GetCursor(m_FrameServer.CoordinateSystem.Stretch));
			}
		}
		private void SetCursor(Cursor _cur)
		{
			pbSurfaceScreen.Cursor = _cur;
		}
		#endregion

		#region Context Menus Events
		
		#region Main
		private void mnuDirectTrack_Click(object sender, EventArgs e)
		{
			// Track the point. No Cross2D was selected.
			// m_DescaledMouse would have been set during the MouseDown event.
			CheckCustomDecodingSize(true);
			Track trk = new Track(m_DescaledMouse, m_iCurrentPosition, m_FrameServer.CurrentImage, m_FrameServer.CurrentImage.Size);
			m_FrameServer.Metadata.AddTrack(trk, OnShowClosestFrame, Color.CornflowerBlue); // todo: get color from track tool.
			
			// Return to the pointer tool.
			m_ActiveTool = m_PointerTool;
			SetCursor(m_PointerTool.GetCursor(0));
			
			DoInvalidate();
		}
		private void mnuSendPic_Click(object sender, EventArgs e)
		{
			// Send the current image to the other screen for conversion into an observational reference.
			if(m_bSynched && m_FrameServer.CurrentImage != null)
			{
				Bitmap img = CloneTransformedImage();
				m_PlayerScreenUIHandler.PlayerScreenUI_SendImage(img);	
			}
		}
		#endregion
		
		#region Drawings Menus
		private void mnuConfigureDrawing_Click(object sender, EventArgs e)
		{
			// Generic menu for all drawings with the Color or ColorSize capability.
			if(m_FrameServer.Metadata.SelectedDrawingFrame >= 0 && 
			   m_FrameServer.Metadata.SelectedDrawing >= 0 &&
			   m_FrameServer.Metadata.Count > m_FrameServer.Metadata.SelectedDrawingFrame)
			{
			    Keyframe kf = m_FrameServer.Metadata[m_FrameServer.Metadata.SelectedDrawingFrame];
			    if(kf.Drawings.Count > m_FrameServer.Metadata.SelectedDrawing)
			    {
			        IDecorable decorableDrawing = kf.Drawings[m_FrameServer.Metadata.SelectedDrawing] as IDecorable;
    				if(decorableDrawing != null && decorableDrawing.DrawingStyle != null && decorableDrawing.DrawingStyle.Elements.Count > 0)
    				{
    					FormConfigureDrawing2 fcd = new FormConfigureDrawing2(decorableDrawing.DrawingStyle, DoInvalidate);
    					ScreenManagerKernel.LocateForm(fcd);
    					fcd.ShowDialog();
    					fcd.Dispose();
    					DoInvalidate();
    				}
			    }
			}
		}
		private void mnuConfigureFading_Click(object sender, EventArgs e)
		{
			// Generic menu for all drawings with the Fading capability.
			if(m_FrameServer.Metadata.SelectedDrawingFrame >= 0 && m_FrameServer.Metadata.SelectedDrawing >= 0)
			{
				formConfigureFading fcf = new formConfigureFading(m_FrameServer.Metadata[m_FrameServer.Metadata.SelectedDrawingFrame].Drawings[m_FrameServer.Metadata.SelectedDrawing], pbSurfaceScreen);
				ScreenManagerKernel.LocateForm(fcf);
				fcf.ShowDialog();
				fcf.Dispose();
				DoInvalidate();
			}
		}
		private void mnuConfigureOpacity_Click(object sender, EventArgs e)
		{
			// Generic menu for all drawings with the Opacity capability.
			if(m_FrameServer.Metadata.SelectedDrawingFrame >= 0 && m_FrameServer.Metadata.SelectedDrawing >= 0)
			{
				formConfigureOpacity fco = new formConfigureOpacity(m_FrameServer.Metadata[m_FrameServer.Metadata.SelectedDrawingFrame].Drawings[m_FrameServer.Metadata.SelectedDrawing], pbSurfaceScreen);
				ScreenManagerKernel.LocateForm(fco);
				fco.ShowDialog();
				fco.Dispose();
				DoInvalidate();
			}
		}
		private void mnuGotoKeyframe_Click(object sender, EventArgs e)
		{
			// Generic menu for all drawings when we are not on their attachement key frame.
			if (m_FrameServer.Metadata.SelectedDrawingFrame >= 0 && m_FrameServer.Metadata.SelectedDrawing >= 0)
			{
				long iPosition = m_FrameServer.Metadata[m_FrameServer.Metadata.SelectedDrawingFrame].Drawings[m_FrameServer.Metadata.SelectedDrawing].infosFading.ReferenceTimestamp;

				m_iFramesToDecode = 1;
				ShowNextFrame(iPosition, true);
				UpdatePositionUI();
				ActivateKeyframe(m_iCurrentPosition);
			}
		}
		private void mnuDeleteDrawing_Click(object sender, EventArgs e)
		{
			// Generic menu for all attached drawings.
			DeleteSelectedDrawing();
		}
		private void DeleteSelectedDrawing()
		{
			if (m_FrameServer.Metadata.SelectedDrawingFrame >= 0 && m_FrameServer.Metadata.SelectedDrawing >= 0)
			{
				IUndoableCommand cdd = new CommandDeleteDrawing(DoInvalidate, m_FrameServer.Metadata, m_FrameServer.Metadata[m_FrameServer.Metadata.SelectedDrawingFrame].Position, m_FrameServer.Metadata.SelectedDrawing);
				CommandManager cm = CommandManager.Instance();
				cm.LaunchUndoableCommand(cdd);
				DoInvalidate();
			}
		}
		
		private void mnuTrackTrajectory_Click(object sender, EventArgs e)
		{
			//---------------------------------------
			// Turn a Cross2D into a Track.
			// Cross2D was selected upon Right Click.
			//---------------------------------------

			// We force the user to be on the right frame.
			if (m_iActiveKeyFrameIndex >= 0 && m_iActiveKeyFrameIndex == m_FrameServer.Metadata.SelectedDrawingFrame)
			{
				int iSelectedDrawing = m_FrameServer.Metadata.SelectedDrawing;

				if (iSelectedDrawing >= 0)
				{
					// TODO - link to CommandAddTrajectory.
					// Add track on this point.
					DrawingCross2D dc = m_FrameServer.Metadata[m_iActiveKeyFrameIndex].Drawings[iSelectedDrawing] as DrawingCross2D;
					if(dc != null)
					{
					    CheckCustomDecodingSize(true);
						Track trk = new Track(dc.Center, m_iCurrentPosition, m_FrameServer.CurrentImage, m_FrameServer.CurrentImage.Size);
						m_FrameServer.Metadata.AddTrack(trk, OnShowClosestFrame, dc.PenColor);
						
						
						// Suppress the point as a Drawing (?)
						m_FrameServer.Metadata[m_iActiveKeyFrameIndex].Drawings.RemoveAt(iSelectedDrawing);
						m_FrameServer.Metadata.Deselect();
	
						// Return to the pointer tool.
						m_ActiveTool = m_PointerTool;
						SetCursor(m_PointerTool.GetCursor(0));
					}
				}
			}
			DoInvalidate();
		}
		#endregion
		
		#region Tracking Menus
		private void mnuStopTracking_Click(object sender, EventArgs e)
		{
			Track trk = m_FrameServer.Metadata.ExtraDrawings[m_FrameServer.Metadata.SelectedExtraDrawing] as Track;
			if(trk != null)
				trk.StopTracking();
			CheckCustomDecodingSize(false);
			DoInvalidate();
		}
		private void mnuDeleteEndOfTrajectory_Click(object sender, EventArgs e)
		{
			IUndoableCommand cdeot = new CommandDeleteEndOfTrack(this, m_FrameServer.Metadata, m_iCurrentPosition);
			CommandManager cm = CommandManager.Instance();
			cm.LaunchUndoableCommand(cdeot);

			DoInvalidate();
			UpdateFramesMarkers();
		}
		private void mnuRestartTracking_Click(object sender, EventArgs e)
		{
			Track trk = m_FrameServer.Metadata.ExtraDrawings[m_FrameServer.Metadata.SelectedExtraDrawing] as Track;
			if(trk == null)
			    return;
			
			CheckCustomDecodingSize(true);
			trk.RestartTracking();
			DoInvalidate();
		}
		private void mnuDeleteTrajectory_Click(object sender, EventArgs e)
		{
			IUndoableCommand cdc = new CommandDeleteTrack(this, m_FrameServer.Metadata);
			CommandManager cm = CommandManager.Instance();
			cm.LaunchUndoableCommand(cdc);
			
			UpdateFramesMarkers();
			CheckCustomDecodingSize(false);
			
			// Trigger a refresh of the export to spreadsheet menu, 
			// in case we don't have any more trajectory left to export.
			OnPoke();
		}
		private void mnuConfigureTrajectory_Click(object sender, EventArgs e)
		{
			Track trk = m_FrameServer.Metadata.ExtraDrawings[m_FrameServer.Metadata.SelectedExtraDrawing] as Track;
			if(trk == null)
			    return;
			
			DelegatesPool dp = DelegatesPool.Instance();
			if (dp.DeactivateKeyboardHandler != null)
				dp.DeactivateKeyboardHandler();

			formConfigureTrajectoryDisplay fctd = new formConfigureTrajectoryDisplay(trk, DoInvalidate);
			fctd.StartPosition = FormStartPosition.CenterScreen;
			fctd.ShowDialog();
			fctd.Dispose();

			if (dp.ActivateKeyboardHandler != null)
				dp.ActivateKeyboardHandler();
		}
		private void OnShowClosestFrame(Point _mouse, List<AbstractTrackPoint> _positions, int _iPixelTotalDistance, bool _b2DOnly)
		{
			//--------------------------------------------------------------------------
			// This is where the interactivity of the trajectory is done.
			// The user has draged or clicked the trajectory, we find the closest point
			// and we update to the corresponding frame.
			//--------------------------------------------------------------------------

			// Compute the 3D distance (x,y,t) of each point in the path.
			// unscaled coordinates.

			double minDistance = double.MaxValue;
			int iClosestPoint = 0;

			if (_b2DOnly)
			{
				// Check the closest location on screen.
				for (int i = 0; i < _positions.Count; i++)
				{
					double dist = Math.Sqrt(((_mouse.X - _positions[i].X) * (_mouse.X - _positions[i].X))
					                        + ((_mouse.Y - _positions[i].Y) * (_mouse.Y - _positions[i].Y)));


					if (dist < minDistance)
					{
						minDistance = dist;
						iClosestPoint = i;
					}
				}
			}
			else
			{
				// Check closest location on screen, but giving priority to the one also close in time.
				// = distance in 3D.
				// Distance on t is not in the same unit as distance on x and y.
				// So first step is to normalize t.

				// _iPixelTotalDistance should be the flat distance (distance from topleft to bottomright)
				// not the added distances of each segments, otherwise it will be biased towards time.

				long TimeTotalDistance = _positions[_positions.Count -1].T - _positions[0].T;
				double scaleFactor = (double)TimeTotalDistance / (double)_iPixelTotalDistance;

				for (int i = 0; i < _positions.Count; i++)
				{
					double fTimeDistance = (double)(m_iCurrentPosition - _positions[i].T);

					double dist = Math.Sqrt(((_mouse.X - _positions[i].X) * (_mouse.X - _positions[i].X))
					                        + ((_mouse.Y - _positions[i].Y) * (_mouse.Y - _positions[i].Y))
					                        + ((long)(fTimeDistance / scaleFactor) * (long)(fTimeDistance / scaleFactor)));

					if (dist < minDistance)
					{
						minDistance = dist;
						iClosestPoint = i;
					}
				}

			}

			// move to corresponding timestamp.
			m_iFramesToDecode = 1;
			ShowNextFrame(_positions[iClosestPoint].T, true);
			UpdatePositionUI();
		}
		#endregion

		#region Chronometers Menus
		private void mnuChronoStart_Click(object sender, EventArgs e)
		{
			IUndoableCommand cmc = new CommandModifyChrono(this, m_FrameServer.Metadata, ChronoModificationType.TimeStart, m_iCurrentPosition);
			CommandManager cm = CommandManager.Instance();
			cm.LaunchUndoableCommand(cmc);
		}
		private void mnuChronoStop_Click(object sender, EventArgs e)
		{
			IUndoableCommand cmc = new CommandModifyChrono(this, m_FrameServer.Metadata, ChronoModificationType.TimeStop, m_iCurrentPosition);
			CommandManager cm = CommandManager.Instance();
			cm.LaunchUndoableCommand(cmc);
			UpdateFramesMarkers();
		}
		private void mnuChronoHide_Click(object sender, EventArgs e)
		{
			IUndoableCommand cmc = new CommandModifyChrono(this, m_FrameServer.Metadata, ChronoModificationType.TimeHide, m_iCurrentPosition);
			CommandManager cm = CommandManager.Instance();
			cm.LaunchUndoableCommand(cmc);
		}
		private void mnuChronoCountdown_Click(object sender, EventArgs e)
		{
			// This menu should only be accessible if we have a "Stop" value.
			mnuChronoCountdown.Checked = !mnuChronoCountdown.Checked;
			
			IUndoableCommand cmc = new CommandModifyChrono(this, m_FrameServer.Metadata, ChronoModificationType.Countdown, (mnuChronoCountdown.Checked == true)?1:0);
			CommandManager cm = CommandManager.Instance();
			cm.LaunchUndoableCommand(cmc);
			
			DoInvalidate();
		}
		private void mnuChronoDelete_Click(object sender, EventArgs e)
		{
			IUndoableCommand cdc = new CommandDeleteChrono(this, m_FrameServer.Metadata);
			CommandManager cm = CommandManager.Instance();
			cm.LaunchUndoableCommand(cdc);
			
			UpdateFramesMarkers();
		}
		private void mnuChronoConfigure_Click(object sender, EventArgs e)
		{
			DrawingChrono dc = m_FrameServer.Metadata.ExtraDrawings[m_FrameServer.Metadata.SelectedExtraDrawing] as DrawingChrono;
			if(dc != null)
			{
				DelegatesPool dp = DelegatesPool.Instance();
				if (dp.DeactivateKeyboardHandler != null)
				{
					dp.DeactivateKeyboardHandler();
				}
				
				// Change this chrono display.
				formConfigureChrono fcc = new formConfigureChrono(dc, DoInvalidate);
				ScreenManagerKernel.LocateForm(fcc);
				fcc.ShowDialog();
				fcc.Dispose();
				DoInvalidate();
	
				if (dp.ActivateKeyboardHandler != null)
				{
					dp.ActivateKeyboardHandler();
				}	
			}
		}
		#endregion

		#region Magnifier Menus
		private void mnuMagnifierQuit_Click(object sender, EventArgs e)
		{
			DisableMagnifier();
			DoInvalidate();
		}
		private void mnuMagnifierDirect_Click(object sender, EventArgs e)
		{
			// Use position and magnification to Direct Zoom.
			// Go to direct zoom, at magnifier zoom factor, centered on same point as magnifier.
			m_FrameServer.CoordinateSystem.Zoom = m_FrameServer.Metadata.Magnifier.MagnificationFactor;
			m_FrameServer.CoordinateSystem.RelocateZoomWindow(m_FrameServer.Metadata.Magnifier.Center);
			DisableMagnifier();
			ToastZoom();
			
			ResizeUpdate(true);
		}
		private void mnuMagnifierChangeMagnification(object sender, EventArgs e)
		{
		    ToolStripMenuItem menu = sender as ToolStripMenuItem;
		    if(menu == null)
		        return;
		    
		    foreach(ToolStripMenuItem m in maginificationMenus)
		        m.Checked = false;
		    
			menu.Checked = true;
			
			m_FrameServer.Metadata.Magnifier.MagnificationFactor = (double)menu.Tag;
			DoInvalidate();
		}
		private void DisableMagnifier()
		{
			// Revert to no magnification.
			m_FrameServer.Metadata.Magnifier.Mode = MagnifierMode.None;
			SetCursor(m_PointerTool.GetCursor(0));
		}
		#endregion

		private void mnuDeleteMultiDrawingItem_Click(object sender, EventArgs e)
		{
            IUndoableCommand cds = new CommandDeleteMultiDrawingItem(this, m_FrameServer.Metadata);
			CommandManager cm = CommandManager.Instance();
			cm.LaunchUndoableCommand(cds);
		}
		#endregion
		
		#region DirectZoom
		private void UnzoomDirectZoom(bool _toast)
		{
			m_FrameServer.CoordinateSystem.ReinitZoom();
			
			m_PointerTool.SetZoomLocation(m_FrameServer.CoordinateSystem.Location);
			if(_toast)
			    ToastZoom();
			ReportForSyncMerge();
			ResizeUpdate(true);
		}
		private void IncreaseDirectZoom()
		{
			if (m_FrameServer.Metadata.Magnifier.Mode != MagnifierMode.None)
				DisableMagnifier();

			m_FrameServer.CoordinateSystem.Zoom = Math.Min(m_FrameServer.CoordinateSystem.Zoom + 0.10f, m_MaxZoomFactor);
			AfterZoomChange();
		}
		private void DecreaseDirectZoom()
		{
			if (!m_FrameServer.CoordinateSystem.Zooming)
			    return;

			m_FrameServer.CoordinateSystem.Zoom = Math.Max(m_FrameServer.CoordinateSystem.Zoom - 0.10f, 1.0f);
			AfterZoomChange();
		}
		private void AfterZoomChange()
		{
		    m_FrameServer.CoordinateSystem.RelocateZoomWindow();
			m_PointerTool.SetZoomLocation(m_FrameServer.CoordinateSystem.Location);
			ToastZoom();
			ReportForSyncMerge();
			
			ResizeUpdate(true);
		}
		#endregion
		
		#region Toasts
		private void ToastZoom()
		{
			m_MessageToaster.SetDuration(750);
			int percentage = (int)(m_FrameServer.CoordinateSystem.Zoom * 100);
			m_MessageToaster.Show(String.Format(ScreenManagerLang.Toast_Zoom, percentage.ToString()));
		}
		private void ToastPause()
		{
			m_MessageToaster.SetDuration(750);
			m_MessageToaster.Show(ScreenManagerLang.Toast_Pause);
		}
		#endregion

		#region Synchronisation specifics
		private void AfterSyncAlphaChange()
		{
			m_SyncMergeMatrix.Matrix00 = 1.0f;
			m_SyncMergeMatrix.Matrix11 = 1.0f;
			m_SyncMergeMatrix.Matrix22 = 1.0f;
			m_SyncMergeMatrix.Matrix33 = m_SyncAlpha;
			m_SyncMergeMatrix.Matrix44 = 1.0f;
			m_SyncMergeImgAttr.SetColorMatrix(m_SyncMergeMatrix, ColorMatrixFlag.Default, ColorAdjustType.Bitmap);
		}
		private void IncreaseSyncAlpha()
		{
		    if(!m_bSyncMerge)
		        return;
		    m_SyncAlpha = Math.Max(m_SyncAlpha - 0.1f, 0.0f);
		    AfterSyncAlphaChange();
		    DoInvalidate();
		}
		private void DecreaseSyncAlpha()
		{
		    if(!m_bSyncMerge)
		        return;
		    m_SyncAlpha = Math.Min(m_SyncAlpha + 0.1f, 1.0f);
		    AfterSyncAlphaChange();
            DoInvalidate();
		}
		private void ReportForSyncMerge()
		{
            if(!m_bSynched)
                return;
            
            // If we are not actually merging, we don't need to clone and send the image.
            // But we still need to report to the screen manager to trigger sync operations.
            Bitmap img = null;
            
            if(m_bSyncMerge && m_FrameServer.CurrentImage != null)
            {
                // We have to re-apply the transformations here, because when drawing in this screen we draw directly on the canvas.
                // (there is no intermediate image that we could reuse here, this might be a future optimization).
                // We need to clone it anyway, so we might aswell do the transform.
                img = CloneTransformedImage();
            }
            
            m_PlayerScreenUIHandler.PlayerScreenUI_ImageChanged(img);
		}
		private Bitmap CloneTransformedImage()
		{
		    // TODO: try to render unscaled here as well when possible.
		    Size copySize = m_viewportManipulator.RenderingSize;
			Bitmap copy = new Bitmap(copySize.Width, copySize.Height);
			Graphics g = Graphics.FromImage(copy);
			
			Rectangle rDst;
			if(m_FrameServer.Metadata.Mirrored)
				rDst = new Rectangle(copySize.Width, 0, -copySize.Width, copySize.Height);
			else
				rDst = new Rectangle(0, 0, copySize.Width, copySize.Height);
			
			if(m_viewportManipulator.MayDrawUnscaled && m_FrameServer.VideoReader.CanDrawUnscaled)
			    g.DrawImage(m_FrameServer.CurrentImage, rDst, m_FrameServer.CoordinateSystem.RenderingZoomWindow, GraphicsUnit.Pixel);
			else
			    g.DrawImage(m_FrameServer.CurrentImage, rDst, m_FrameServer.CoordinateSystem.ZoomWindow, GraphicsUnit.Pixel);
                
			return copy;
		}
		#endregion
		
		#region VideoFilters Management
		private void EnableDisableAllPlayingControls(bool _bEnable)
		{
			// Disable playback controls and some other controls for the case
			// of a one-frame rendering. (mosaic, single image)
			
			if(m_FrameServer.Loaded && !m_FrameServer.VideoReader.CanChangeWorkingZone)
                EnableDisableWorkingZoneControls(false);
			else
                EnableDisableWorkingZoneControls(_bEnable);
			
			buttonGotoFirst.Enabled = _bEnable;
			buttonGotoLast.Enabled = _bEnable;
			buttonGotoNext.Enabled = _bEnable;
			buttonGotoPrevious.Enabled = _bEnable;
			buttonPlay.Enabled = _bEnable;
			buttonPlayingMode.Enabled = _bEnable;
			
			lblSpeedTuner.Enabled = _bEnable;
			trkFrame.EnableDisable(_bEnable);
			
			sldrSpeed.EnableDisable(_bEnable);
			trkFrame.Enabled = _bEnable;
			trkSelection.Enabled = _bEnable;
			sldrSpeed.Enabled = _bEnable;
			
			btnRafale.Enabled = _bEnable;
			btnSaveVideo.Enabled = _bEnable;
			btnDiaporama.Enabled = _bEnable;
			btnPausedVideo.Enabled = _bEnable;
			
			mnuPlayPause.Visible = _bEnable;
			mnuDirectTrack.Visible = _bEnable;
		}
		private void EnableDisableWorkingZoneControls(bool _bEnable)
		{
            btnSetHandlerLeft.Enabled = _bEnable;
			btnSetHandlerRight.Enabled = _bEnable;
			btnHandlersReset.Enabled = _bEnable;
			btn_HandlersLock.Enabled = _bEnable;
			trkSelection.EnableDisable(_bEnable);
		}
		private void EnableDisableSnapshot(bool _bEnable)
		{
			btnSnapShot.Enabled = _bEnable;
		}
		private void EnableDisableDrawingTools(bool _bEnable)
		{
			foreach(ToolStripItem tsi in stripDrawingTools.Items)
			{
				tsi.Enabled = _bEnable;
			}
		}
		#endregion
		
		#region Export video and frames
		private void btnSnapShot_Click(object sender, EventArgs e)
		{
			// Export the current frame.
			if (!m_FrameServer.Loaded || m_FrameServer.CurrentImage == null)
			    return;
			
			StopPlaying();
			m_PlayerScreenUIHandler.PlayerScreenUI_PauseAsked();
			
			try
			{
				SaveFileDialog dlgSave = new SaveFileDialog();
				dlgSave.Title = ScreenManagerLang.Generic_SaveImage;
				dlgSave.RestoreDirectory = true;
				dlgSave.Filter = ScreenManagerLang.dlgSaveFilter;
				dlgSave.FilterIndex = 1;
				
				if(InteractiveFiltering)
					dlgSave.FileName = Path.GetFileNameWithoutExtension(m_FrameServer.VideoReader.FilePath);
				else
					dlgSave.FileName = BuildFilename(m_FrameServer.VideoReader.FilePath, m_iCurrentPosition, m_PrefManager.TimeCodeFormat);
				
				if (dlgSave.ShowDialog() == DialogResult.OK)
				{
				    Bitmap outputImage = GetFlushedImage();
					ImageHelper.Save(dlgSave.FileName, outputImage);
					outputImage.Dispose();
					m_FrameServer.AfterSave();
				}
			}
			catch (Exception exp)
			{
				log.Error(exp.StackTrace);
			}
		}
		private void btnRafale_Click(object sender, EventArgs e)
		{
			//---------------------------------------------------------------------------------
			// Workflow:
			// 1. formRafaleExport  : configure the export, calls:
			// 2. FileSaveDialog    : choose the file name, then:
			// 3. formFrameExport   : Progress bar holder and updater, calls:
			// 4. SaveImageSequence (below) to perform the real work. (saving the pics)
			//---------------------------------------------------------------------------------

			if (!m_FrameServer.Loaded || m_FrameServer.CurrentImage == null)
			    return;
			
			StopPlaying();
			m_PlayerScreenUIHandler.PlayerScreenUI_PauseAsked();
			DelegatesPool dp = DelegatesPool.Instance();
			if (dp.DeactivateKeyboardHandler != null)
				dp.DeactivateKeyboardHandler();
			
			// Launch sequence saving configuration dialog
			formRafaleExport fre = new formRafaleExport(this, 
			                                            m_FrameServer.Metadata, 
			                                            m_FrameServer.VideoReader.FilePath, 
			                                            m_iSelDuration, 
			                                            m_FrameServer.VideoReader.Info.AverageTimeStampsPerSeconds);
			fre.ShowDialog();
			fre.Dispose();
			m_FrameServer.AfterSave();
			
			if (dp.ActivateKeyboardHandler != null)
				dp.ActivateKeyboardHandler();
			
			m_iFramesToDecode = 1;
			ShowNextFrame(m_iSelStart, true);
			ActivateKeyframe(m_iCurrentPosition, true);
		}
		public void SaveImageSequence(BackgroundWorker _bgWorker, string _filepath, long _interval, bool _bBlendDrawings, bool _bKeyframesOnly, int _total)
		{
			int total = _bKeyframesOnly ? m_FrameServer.Metadata.Keyframes.Count : _total;
            int iCurrent = 0;
            
		    // Use an abstracted enumerator on the frames we are interested in.
			// Either the keyframes or arbitrary frames at regular interval.
			m_FrameServer.VideoReader.BeforeFrameEnumeration();
			IEnumerable<VideoFrame> frames = _bKeyframesOnly ? m_FrameServer.Metadata.EnabledKeyframes() : m_FrameServer.VideoReader.FrameEnumerator(_interval);
			
			foreach(VideoFrame vf in frames)
            {
                int iKeyFrameIndex = -1;
                if(!m_PrefManager.DefaultFading.Enabled)
                    iKeyFrameIndex = GetKeyframeIndex(vf.Timestamp);

                string fileName = string.Format("{0}\\{1}{2}",
                    Path.GetDirectoryName(_filepath), 
                    BuildFilename(_filepath, vf.Timestamp, m_PrefManager.TimeCodeFormat), 
                    Path.GetExtension(_filepath));
                
                Size s = m_viewportManipulator.RenderingSize;
                    
                using(Bitmap result = new Bitmap(s.Width, s.Height, PixelFormat.Format24bppRgb))
                {
                    result.SetResolution(vf.Image.HorizontalResolution, vf.Image.VerticalResolution);
                    Graphics g = Graphics.FromImage(result);
                    FlushOnGraphics(vf.Image, g, s, iKeyFrameIndex, vf.Timestamp);
                    ImageHelper.Save(fileName, result);
                }

                _bgWorker.ReportProgress(iCurrent++, total);
			}
			
			m_FrameServer.VideoReader.AfterFrameEnumeration();
		}
		private int GetKeyframeIndex(long _timestamp)
		{
		    // Get the index of the kf we are on, if any.
            int index = -1;
            for(int i = 0;i<m_FrameServer.Metadata.Count;i++)
            {
                if (m_FrameServer.Metadata[i].Position == _timestamp)
                {
                    index = i;
                    break;
                }
            }
            return index;
		}
		private void btnVideo_Click(object sender, EventArgs e)
		{
			if(!m_FrameServer.Loaded)
			    return;
			
			StopPlaying();
			m_PlayerScreenUIHandler.PlayerScreenUI_PauseAsked();
			
			DelegatesPool dp = DelegatesPool.Instance();
			if (dp.DeactivateKeyboardHandler != null)
				dp.DeactivateKeyboardHandler();
			
			Save();
			
			if (dp.ActivateKeyboardHandler != null)
				dp.ActivateKeyboardHandler();
			
			m_iFramesToDecode = 1;
			ShowNextFrame(m_iSelStart, true);
			ActivateKeyframe(m_iCurrentPosition, true);
		}
		private void btnDiaporama_Click(object sender, EventArgs e)
		{
		    if (!m_FrameServer.Loaded || m_FrameServer.CurrentImage == null)
		        return;
		        
			bool diaporama = sender == btnDiaporama;
			
			StopPlaying();
			m_PlayerScreenUIHandler.PlayerScreenUI_PauseAsked();
			
			if(m_FrameServer.Metadata.Keyframes.Count < 1)
			{
			    string error = diaporama ? ScreenManagerLang.Error_SavePausedVideo : ScreenManagerLang.Error_SavePausedVideo;
				MessageBox.Show(ScreenManagerLang.Error_SaveDiaporama_NoKeyframes.Replace("\\n", "\n"),
                                error,
					            MessageBoxButtons.OK,
					            MessageBoxIcon.Exclamation);
			    return;
			}
			
			DelegatesPool dp = DelegatesPool.Instance();
			if (dp.DeactivateKeyboardHandler != null)
				dp.DeactivateKeyboardHandler();
				
			m_FrameServer.SaveDiaporama(GetOutputBitmap, diaporama);

			if (dp.ActivateKeyboardHandler != null)
				dp.ActivateKeyboardHandler();
			
			m_iFramesToDecode = 1;
			ShowNextFrame(m_iSelStart, true);
			ActivateKeyframe(m_iCurrentPosition, true);
		}
		public void Save()
		{
			m_FrameServer.Save(GetPlaybackFrameInterval(), m_fSlowmotionPercentage, GetOutputBitmap);
		}
		public long GetOutputBitmap(Graphics _canvas, Bitmap _source, long _iTimestamp, bool _bFlushDrawings, bool _bKeyframesOnly)
		{
			// Used by various methods to paint the drawings on an already retrieved raw image.
			// The source image is already drawn on _canvas.
			// Here we we flush the drawings on it if needed.
			// We return the distance to the closest key image.

			// Look for the closest key image.
			long iClosestKeyImageDistance = long.MaxValue;	
			int iKeyFrameIndex = -1;
			for(int i=0; i<m_FrameServer.Metadata.Keyframes.Count;i++)
			{
				long iDistance = Math.Abs(_iTimestamp - m_FrameServer.Metadata.Keyframes[i].Position);
				if(iDistance < iClosestKeyImageDistance)
				{
					iClosestKeyImageDistance = iDistance;
					iKeyFrameIndex = i;
				}
			}

			// Invalidate the distance if we wanted only key images, and we are not on one.
			if (_bKeyframesOnly && iClosestKeyImageDistance != 0 || iClosestKeyImageDistance == long.MaxValue)
				iClosestKeyImageDistance = -1;
			
			if(!_bFlushDrawings)
			    return iClosestKeyImageDistance;
			
			// For magnifier we must clone the image since the graphics object has been
			// extracted from the image itself (painting fails if we reuse the uncloned image).
			// And we must clone it before the drawings are flushed on it.
			bool magnifier = m_FrameServer.Metadata.Magnifier.Mode != MagnifierMode.None;
			Bitmap rawImage = null;
			if(magnifier)
			    rawImage = _source.CloneDeep();

            CoordinateSystem identityCoordinateSystem = m_FrameServer.CoordinateSystem.Identity;

			if (_bKeyframesOnly)
			{
				if(iClosestKeyImageDistance == 0)
				{
                    FlushDrawingsOnGraphics(_canvas, identityCoordinateSystem, iKeyFrameIndex, _iTimestamp);
                    if(magnifier)
                        FlushMagnifierOnGraphics(rawImage, _canvas, identityCoordinateSystem);
				}
			}
			else
			{
				if(iClosestKeyImageDistance == 0)
				    FlushDrawingsOnGraphics(_canvas, identityCoordinateSystem, iKeyFrameIndex, _iTimestamp);
				else
					FlushDrawingsOnGraphics(_canvas, identityCoordinateSystem, -1, _iTimestamp);
				
				if(magnifier)
                    FlushMagnifierOnGraphics(rawImage, _canvas, identityCoordinateSystem);
			}	

            if(magnifier)
                rawImage.Dispose();
			
			return iClosestKeyImageDistance;
		}
		public Bitmap GetFlushedImage()
		{
			// Returns an image with all drawings flushed, including
			// grids, chronos, magnifier, etc.
			// image should be at same strech factor than the one visible on screen.
			//Size iNewSize = new Size((int)((double)m_FrameServer.CurrentImage.Width * m_FrameServer.CoordinateSystem.Stretch), (int)((double)m_FrameServer.CurrentImage.Height * m_FrameServer.CoordinateSystem.Stretch));
			Size renderingSize = m_viewportManipulator.RenderingSize;
			
			Bitmap output = new Bitmap(renderingSize.Width, renderingSize.Height, PixelFormat.Format24bppRgb);
			output.SetResolution(m_FrameServer.CurrentImage.HorizontalResolution, m_FrameServer.CurrentImage.VerticalResolution);
			
			if(InteractiveFiltering)
			{
			    m_InteractiveEffect.Draw(Graphics.FromImage(output), m_FrameServer.VideoReader.WorkingZoneFrames);
			}
			else
			{
				int iKeyFrameIndex = -1;
				if (m_iActiveKeyFrameIndex >= 0 && m_FrameServer.Metadata[m_iActiveKeyFrameIndex].Drawings.Count > 0)
				{
					iKeyFrameIndex = m_iActiveKeyFrameIndex;
				}				
				
				FlushOnGraphics(m_FrameServer.CurrentImage, Graphics.FromImage(output), renderingSize, iKeyFrameIndex, m_iCurrentPosition);
			}
			
			return output;
		}
		private string BuildFilename(string _FilePath, long _position, TimeCodeFormat _timeCodeFormat)
		{
			//-------------------------------------------------------
			// Build a file name, including extension
			// inserting the current timecode in the given file name.
			//-------------------------------------------------------

			TimeCodeFormat tcf;
			if(_timeCodeFormat == TimeCodeFormat.TimeAndFrames)
				tcf = TimeCodeFormat.ClassicTime;
			else
				tcf = _timeCodeFormat;
			
			// Timecode string (Not relative to sync position)
			string suffix = TimeStampsToTimecode(_position - m_iSelStart, tcf, false);
			string maxSuffix = TimeStampsToTimecode(m_iSelEnd - m_iSelStart, tcf, false);

			switch (tcf)
			{
				case TimeCodeFormat.Frames:
				case TimeCodeFormat.Milliseconds:
				case TimeCodeFormat.TenThousandthOfHours:
				case TimeCodeFormat.HundredthOfMinutes:
					
					int iZerosToPad = maxSuffix.Length - suffix.Length;
					for (int i = 0; i < iZerosToPad; i++)
					{
						// Add a leading zero.
						suffix = suffix.Insert(0, "0");
					}
					break;
				default:
					break;
			}

			// Reconstruct filename
			return Path.GetFileNameWithoutExtension(_FilePath) + "-" + suffix.Replace(':', '.');
		}
		#endregion

		#region Memo & Reset
		public MemoPlayerScreen GetMemo()
		{
			return new MemoPlayerScreen(m_iSelStart, m_iSelEnd);
		}
		public void ResetSelectionImages(MemoPlayerScreen _memo)
		{
			// This is typically called when undoing image adjustments.
			// We do not actually undo the adjustment because we don't have the original data anymore.
			// We emulate it by reloading the selection.
			
			// Memorize the current selection boundaries.
			MemoPlayerScreen mps = new MemoPlayerScreen(m_iSelStart, m_iSelEnd);

			// Reset the selection to whatever it was when we did the image adjustment.
			m_iSelStart = _memo.SelStart;
			m_iSelEnd = _memo.SelEnd;

			// Undo all adjustments made on this portion.
			UpdateWorkingZone(true);
			UpdateKeyframes();

			// Reset to the current selection.
			m_iSelStart = mps.SelStart;
			m_iSelEnd = mps.SelEnd;
		}
		#endregion

	}
}
