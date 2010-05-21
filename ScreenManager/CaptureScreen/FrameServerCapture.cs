﻿#region License
/*
Copyright © Joan Charmant 2008-2009.
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
using Kinovea.ScreenManager.Languages;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Windows.Forms;
using Kinovea.Services;
using Kinovea.VideoFiles;

namespace Kinovea.ScreenManager
{
	/// <summary>
	/// FrameServerCapture encapsulates all the metadata and configuration for managing frames in a capture screen.
	/// This is the object that maintains the interface with file level operations done by VideoFile class.
	/// </summary>
	public class FrameServerCapture : AbstractFrameServer, IFrameGrabberContainer
	{
		#region Properties
		
		// Capture device.
		public bool IsConnected
		{
			get { return m_FrameGrabber.IsConnected; }
		}
		public bool IsGrabbing
		{
			get {return m_FrameGrabber.IsGrabbing;}
		}
		public Size ImageSize
		{
			get { return m_ImageSize; }
		}
		
		// Drawings and other screens overlays.
		public Metadata Metadata
		{
			get { return m_Metadata; }
			set { m_Metadata = value; }
		}
		public Magnifier Magnifier
		{
			get { return m_Magnifier; }
			set { m_Magnifier = value; }
		}
		public CoordinateSystem CoordinateSystem
		{
			get { return m_CoordinateSystem; }
		}
		
		// Saving to disk.
		public List<CapturedVideo> RecentlyCapturedVideos
		{
			get { return m_RecentlyCapturedVideos; }	
		}
		#endregion
		
		#region Members
		private IFrameServerContainer m_Container;	// CaptureScreenUserInterface seen through a limited interface.
		
		// Grabbing frames
		//private FrameGrabberAForge m_FrameGrabber;
		private AbstractFrameGrabber m_FrameGrabber;
		private FrameBuffer m_FrameBuffer = new FrameBuffer();
		private Bitmap m_MostRecentImage;
		private Size m_ImageSize = new Size(720, 576);
		
		// Drawings and other screens overlays.
		private bool m_bPainting;									// 'true' between paint requests.
		private Metadata m_Metadata;
		private Magnifier m_Magnifier = new Magnifier();
		private CoordinateSystem m_CoordinateSystem = new CoordinateSystem();
		
		// Saving to disk
		private List<CapturedVideo> m_RecentlyCapturedVideos = new List<CapturedVideo>();

		// todo: evaluate after end of refactoring.
		/*
		private int m_iDelayFrames = 0;							// Delay between what is captured and what is seen on screen.
		private bool m_bIsRecording;
		private Bitmap m_CurrentCaptureBitmap;						// Used to create the thumbnail.
		private string m_CurrentCaptureFilePath;					// Used to create the thumbnail.
		private VideoFileWriter m_VideoFileWriter = new VideoFileWriter();
		*/

		
		// General
		private static readonly log4net.ILog log = log4net.LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);
		#endregion
		
		#region Constructor
		public FrameServerCapture()
		{
			m_FrameGrabber = new FrameGrabberAForge(this, m_FrameBuffer);
			m_FrameGrabber.NegociateDevice();
		}
		#endregion
		
		#region Implementation of IFrameGrabberContainer
		public void Connected()
		{
			log.Debug("Screen connected.");
			StartGrabbing();
			// FIXME: notify the UI to change its display.
		}
		public void SetImageSize(Size _size)
		{
			m_ImageSize = _size;
			m_CoordinateSystem.SetOriginalSize(m_ImageSize);
			m_Container.DoInitDecodingSize();
		}
		public void FrameGrabbed()
		{
			// The frame grabber has just pushed a new frame to the buffer.
			
			// Consolidate this real-time frame locally.
			m_MostRecentImage = m_FrameBuffer.ReadFrameAt(0);
			
			// Ask a refresh. This could also be done with a timer,
			// but using the frame grabber event is convenient.
			if(!m_bPainting)
			{
				m_bPainting = true;
				m_Container.DoInvalidate();
			}
			
			//If recording, append the new frame to file.
			/*if(m_bIsRecording)
			{
				m_VideoFileWriter.SaveFrame(m_FrameBuffer[m_FrameBuffer.Count-1]);
				if(m_CurrentCaptureBitmap == null)
				{
					m_CurrentCaptureBitmap = m_FrameBuffer[m_FrameBuffer.Count-1];
				}
			}*/
		}
		public void AlertCannotConnect()
		{
			// Couldn't find device. Signal to user.
			MessageBox.Show(
        		"Couldn't find any device to connect to.\nPlease make sure the device is properly connected.",
               	"Cannot connect to video device",
               	MessageBoxButtons.OK,
                MessageBoxIcon.Exclamation);
		}
		#endregion
		
		#region Public methods
		public void SetContainer(IFrameServerContainer _container)
		{
			m_Container = _container;
		}
		public void NegociateDevice()
		{
			m_FrameGrabber.NegociateDevice();
		}
		public void StartGrabbing()
		{
			//m_FrameBuffer.Clear();
			m_FrameGrabber.StartGrabbing();
		}
		public void PauseGrabbing()
		{
			m_FrameGrabber.PauseGrabbing();
		}
		public void BeforeClose()
		{
			m_FrameGrabber.BeforeClose();
		}
		public override void Draw(Graphics _canvas)
		{
			// Draw the current image on canvas according to conf.
			// This is called back from UI paint method.

			// Todo: maybe the drawings should be added directly inside GetImageToDisplay().
			// This way we would call it directly when saving to disk.
			
			if(m_FrameGrabber.IsConnected)
			{
				Bitmap image = GetImageToDisplay();
				
				if(image != null)
				{
					// Configure canvas.
					_canvas.PixelOffsetMode = PixelOffsetMode.HighSpeed;
					_canvas.CompositingQuality = CompositingQuality.HighSpeed;
					_canvas.InterpolationMode = InterpolationMode.Bilinear;
					_canvas.SmoothingMode = SmoothingMode.None;	
					
					try
					{
						// Draw image.
						Rectangle rDst;
						/*if(m_FrameServer.Metadata.Mirrored)
						{
							rDst = new Rectangle(_iNewSize.Width, 0, -_iNewSize.Width, _iNewSize.Height);
						}
						else
						{
							rDst = new Rectangle(0, 0, _iNewSize.Width, _iNewSize.Height);
						}*/
						
						rDst = new Rectangle((int)_canvas.ClipBounds.Left, (int)_canvas.ClipBounds.Top, (int)_canvas.ClipBounds.Width, (int)_canvas.ClipBounds.Height);
						
						RectangleF rSrc;
						if (m_CoordinateSystem.Zooming)
						{
							rSrc = m_CoordinateSystem.ZoomWindow;
						}
						else
						{
							rSrc = new Rectangle(0, 0, m_ImageSize.Width, m_ImageSize.Height);
						}
						
						_canvas.DrawImage(image, _canvas.ClipBounds, rSrc, GraphicsUnit.Pixel);
						
						FlushDrawingsOnGraphics(_canvas);
						
						// .Magnifier
						// TODO: handle miroring.
						if (m_Magnifier.Mode != MagnifierMode.NotVisible)
						{
							m_Magnifier.Draw(image, _canvas, 1.0, false);
						}
					}
					catch (Exception exp)
					{
						log.Error("Error while painting image.");
						log.Error(exp.Message);
						log.Error(exp.StackTrace);
					}		
				}	
			}
			
			m_bPainting = false;
		}
		public void ToggleRecord()
		{
			// Start recording.
			// We always record what is displayed on screen, not what is grabbed by the device.
			
			
			/*if(m_bIsRecording)
			{
				// Stop recording
				m_bIsRecording = false;
				
				// Close the recording context.
				m_VideoFileWriter.CloseSavingContext(true);
				
				// Move to new name
				
				// Add a VideofileBox (in the Keyframes panel) with a thumbnail of this video.
				// As for KeyframeBox, you'd be able to edit the filename.
				// double click = open it in a Playback screen.
				// time label would be the duration.
				// using the close button do not delete the file, it just hides it.
				
				CapturedVideo cv = new CapturedVideo(m_CurrentCaptureFilePath, m_CurrentCaptureBitmap);
				m_RecentlyCapturedVideos.Add(cv);
				m_CurrentCaptureBitmap = null;
				
				m_Container.DoUpdateCapturedVideos();
			}
			else
			{
				// Restart capturing if needed.
				if(!m_VideoDevice.IsRunning)
				{
					SignalToStart();
				}
				
				// Open a recording context. (on which file name ?)
				// Create filename from current date time.
				string timecode = DateTime.Now.ToString("yyyy-MM-dd HHmmss", CultureInfo.InvariantCulture);
				m_CurrentCaptureFilePath = PreferencesManager.SettingsFolder + "\\" + timecode + ".avi";
				
				SaveResult result = m_VideoFileWriter.OpenSavingContext(m_CurrentCaptureFilePath, null, -1, false);
				
				if(result == SaveResult.Success)
				{
					m_bIsRecording = true;
				}
				else
				{
					m_VideoFileWriter.CloseSavingContext(false);
					m_bIsRecording = false;	
					DisplayError(result);
				}
				
				// If preroll is enabled, flush buffer to file now.
				
			}*/
		}
		#endregion
		
		#region Final image creation
		private Bitmap GetImageToDisplay()
		{
			// Get the final image to display, according to delay, compositing, etc.
			
			return m_MostRecentImage;
		}
		private void FlushDrawingsOnGraphics(Graphics _canvas)
		{
			// Commit drawings on image.
			// In capture mode, all drawings are gathered in a virtual key image at m_Metadata[0].
			
			_canvas.SmoothingMode = SmoothingMode.AntiAlias;

			// 1. 2D Grid
			if (m_Metadata.Grid.Visible)
			{
				m_Metadata.Grid.Draw(_canvas, m_CoordinateSystem.Stretch * m_CoordinateSystem.Zoom, m_CoordinateSystem.Location);
			}

			// 2. 3D Plane
			if (m_Metadata.Plane.Visible)
			{
				m_Metadata.Plane.Draw(_canvas, m_CoordinateSystem.Stretch * m_CoordinateSystem.Zoom, m_CoordinateSystem.Location);
			}

			// Draw all drawings in reverse order to get first object on the top of Z-order.
			for (int i = m_Metadata[0].Drawings.Count - 1; i >= 0; i--)
			{
				bool bSelected = (i == m_Metadata.SelectedDrawing);
				m_Metadata[0].Drawings[i].Draw(_canvas, m_CoordinateSystem.Stretch * m_CoordinateSystem.Zoom, bSelected, 0, m_CoordinateSystem.Location);
			}
		}
		#endregion
		
		#region Saving to disk
		private void DisplayError(SaveResult _result)
		{
			switch(_result)
        	{
                case SaveResult.FileHeaderNotWritten:
                case SaveResult.FileNotOpened:
                    DisplayErrorMessage(ScreenManagerLang.Error_SaveMovie_FileError);
                    break;
                
                case SaveResult.EncoderNotFound:
                case SaveResult.EncoderNotOpened:
                case SaveResult.EncoderParametersNotAllocated:
                case SaveResult.EncoderParametersNotSet:
                case SaveResult.InputFrameNotAllocated:
                case SaveResult.MuxerNotFound:
                case SaveResult.MuxerParametersNotAllocated:
                case SaveResult.MuxerParametersNotSet:
                case SaveResult.VideoStreamNotCreated:
                case SaveResult.UnknownError:
                default:
                    DisplayErrorMessage(ScreenManagerLang.Error_SaveMovie_LowLevelError);
                    break;
        	}
		}
		private void DisplayErrorMessage(string _err)
        {
        	MessageBox.Show(
        		_err.Replace("\\n", "\n"),
               	ScreenManagerLang.Error_SaveMovie_Title,
               	MessageBoxButtons.OK,
                MessageBoxIcon.Exclamation);
        }
		#endregion
	}
}
