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
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Imaging;
using System.Threading;

namespace Kinovea.Video
{
	/// <summary>
	/// A base class for all video decoders implementations.
	/// 
	/// Concrete implementations should add a SupportedExtensions attribute listing the extensions
	/// supported by this particular reader, as an array of string.
	/// Ex: [SupportedExtensions(new string[] {".avi", ".bmp"})]
	/// </summary>
	public abstract class VideoReader
	{
	    #region Properties
	    public abstract VideoReaderFlags Flags { get; }
	    public abstract bool Loaded { get; }
		public abstract VideoInfo Info { get; }
		public abstract VideoSection WorkingZone { get; set; }
		#endregion
		
		#region Methods
		public abstract OpenVideoResult Open(string _filePath);
		public abstract void Close();
		
		/// <summary>
		/// Set the "Current" property to hold the next video frame.
		/// For async readers, if the frame is not available right now, call it a drop.
		/// (Decoding should happen in a separate thread).
		/// _synchronous will be true for some scenarios like saving. In this case return only after the frame has been set in Current.
		/// </summary>
		public abstract bool MoveNext(bool _synchronous);
		
		/// <summary>
		/// Set the "Current" property to hold an arbitrary video frame, based on timestamp.
		/// Unlike MoveNext(), this function is always synchronous.
		/// Don't return until you have found the frame and updated "Current" with it.
		/// </summary>
		public abstract bool MoveTo(long _timestamp);
		public abstract VideoSummary ExtractSummary(string _filePath, int _thumbs, int _width);
		public abstract string ReadMetadata();
		public abstract bool CanCacheWorkingZone(VideoSection _newZone, int _maxSeconds, int _maxMemory);
		
		/// <summary>
		/// Import several frames in sequence to cache.
		/// Used in the context of analysis mode (full working zone to cache)
		/// </summary>
		/// <param name="_bgWorker">Hosting background worker, for cancellation and progress</param>
		/// <param name="_section">The section to import</param>
		/// <param name="_prepend">true if the section is before what's currently in the cache, used to configure Cache.Add.</param>
		/// <returns>true if all went fine</returns>
		public abstract bool ReadMany(BackgroundWorker _bgWorker, VideoSection _section, bool _prepend);
		#endregion
		
		#region Concrete Properties
		public VideoFrameCache Cache { get; protected set; }
		public VideoOptions Options { get; set; }
		public bool Caching { get; protected set; }
		public VideoFrame Current {
		    get { 
		        if(Cache == null) return null;
		        else return Cache.Current;
		    }
		}
		public string FilePath {
			get { return Info.FilePath; }
		}
		public bool SingleFrame { 
		    get { return Info.DurationTimeStamps == 1;}
        }
        public Bitmap CurrentImage { 
		    get { 
		        if(Cache == null || Cache.Current == null) return null;
		        else return Cache.Current.Image;
		    }
        }
		#endregion

		public const PixelFormat DecodingPixelFormat = PixelFormat.Format32bppPArgb;
		
		#region Members
		private static readonly log4net.ILog log = log4net.LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);
        #endregion
		
		#region Concrete Methods
		public bool MovePrev()
		{
		    return MoveTo(Current.Timestamp - Info.AverageTimeStampsPerFrame);
		}
		public bool MoveFirst()
		{
		    return MoveTo(WorkingZone.Start);
		}
		public bool MoveLast()
		{
		    return MoveTo(WorkingZone.End);
		}
		public bool MoveBy(int _frames)
		{
		    if(_frames == 1)
		    {
		        return MoveNext(false);
		    }
		    else
		    {
		        long currentTimestamp = Current == null ? 0 : Current.Timestamp;
		        long target = currentTimestamp + (Info.AverageTimeStampsPerFrame * _frames);
		        if(target < 0)
		            target = 0;
		        return MoveTo(target);
		    }
		}
		/// <summary>
		/// Force a specific aspect ratio.
		/// </summary>
		/// <returns>returns true if the cache has been invalidated by the operation</returns>
		public virtual bool ChangeAspectRatio(ImageAspectRatio _ratio)
		{
            // Does nothing by default. Override to implement.
            return false;
		}
		/// <summary>
		/// Set deinterlace on or off.
		/// </summary>
		/// <returns>returns true if the cache has been invalidated by the operation</returns>
		public virtual bool ChangeDeinterlace(bool _deint)
		{
		    // Does nothing by default. Override to implement.
            return false;
		}
		
		/// <summary>
		/// Updates the internal working zone. Import whole zone to cache if possible.
		/// </summary>
		/// <param name="_progressWorker">A function that will start a background thread for the actual import</param>
		public virtual void UpdateWorkingZone(VideoSection _newZone, bool _forceReload, int _maxSeconds, int _maxMemory, Action<DoWorkEventHandler> _workerFn)
        {
            if((Flags & VideoReaderFlags.AlwaysCaching) != 0)
                return;
            
            if(_workerFn == null)
                throw new ArgumentNullException("workerFn");
            
            VideoSection oldZone = WorkingZone;
            WorkingZone = _newZone;
            
            if(!CanCacheWorkingZone(_newZone, _maxSeconds, _maxMemory))
            {
                Caching = false;
                Cache.Clear();
                return;
            }
            
            VideoSection sectionToCache = VideoSection.Empty;
            bool prepend = false;
            
            if(!Caching || _forceReload)
            {
                // Just entering the cached mode, import everything.
                Cache.Clear();
                sectionToCache = _newZone;
            }
            else if(oldZone.Contains(_newZone))
            {
                Cache.PurgeOutsiders();
            }
            else if(_newZone.Start < oldZone.Start && _newZone.End > oldZone.End)
            {
                // Special case of both prepend and append. Clear and import all for simplicity.
                Cache.Clear();
                sectionToCache = _newZone;
            }
            else if(_newZone.Start < oldZone.Start)
            {
                // Prepending.
                sectionToCache = new VideoSection(_newZone.Start, oldZone.Start);
                prepend = true;
            }
            else
            {
                // Appending.
                sectionToCache = new VideoSection(oldZone.End, _newZone.End);
            }
            
            if(sectionToCache != VideoSection.Empty)
            {
                _workerFn((s,e) => Caching = ReadMany((BackgroundWorker)s, sectionToCache, prepend));
            }
		}
		#endregion
	}
}
