#pragma region License
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
#pragma endregion

#include <msclr\lock.h>
#include "VideoReaderFFMpeg.h"

using namespace System::Diagnostics;
using namespace System::Drawing;
using namespace System::Drawing::Drawing2D;
using namespace System::IO;
using namespace System::Runtime::InteropServices;
using namespace System::Collections::Generic;
using namespace System::Threading;
using namespace msclr;

using namespace Kinovea::Video::FFMpeg;

VideoReaderFFMpeg::VideoReaderFFMpeg()
{
    av_register_all();
    avfilter_register_all();
    m_Locker = gcnew Object();
    m_PreBufferingThreadCanceler = gcnew ThreadCanceler();
    
    VideoFrameDisposer^ disposer = gcnew VideoFrameDisposer(DisposeFrame);
    m_SingleFrameContainer = gcnew SingleFrame(disposer);
    m_PreBuffer = gcnew PreBuffer(disposer);
    m_Cache = gcnew Cache(disposer);

    m_LoopWatcher = gcnew LoopWatcher();
    DataInit();
}
VideoReaderFFMpeg::~VideoReaderFFMpeg()
{
    this->!VideoReaderFFMpeg();
}
VideoReaderFFMpeg::!VideoReaderFFMpeg()
{
    if(m_bIsLoaded) 
        Close();
}
OpenVideoResult VideoReaderFFMpeg::Open(String^ _filePath)
{
    OpenVideoResult result = Load(_filePath, false);
    if(result == OpenVideoResult::Success)
        DumpInfo();
    return result;
}
void VideoReaderFFMpeg::Close()
{
    // Unload the video and dispose unmanaged resources.
    if(!m_bIsLoaded)
        return;
        
    DataInit();

    if(m_pCodecCtx != nullptr)
        avcodec_close(m_pCodecCtx);
    
    if(m_pFormatCtx != nullptr)
    {
        AVFormatContext* pin = m_pFormatCtx;
        avformat_close_input(&pin);
        m_pFormatCtx = pin;
    }
}
void VideoReaderFFMpeg::DataInit()
{
    SwitchDecodingMode(VideoDecodingMode::NotInitialized);
    m_bIsLoaded = false;
    m_iVideoStream = -1;
    m_iAudioStream = -1;
    m_iMetadataStream = -1;
    m_VideoInfo = VideoInfo::Empty;
    m_WorkingZone = VideoSection::Empty;
    m_TimestampInfo = TimestampInfo::Empty;
    m_WasPrebuffering = false;
    m_CanDrawUnscaled = false;
}
VideoSummary^ VideoReaderFFMpeg::ExtractSummary(String^ _filePath, int _thumbs, Size _maxSize)
{
    // Open the file and extract some info + a few thumbnails.
    VideoSummary^ summary = gcnew VideoSummary(_filePath);

    OpenVideoResult loaded = Load(_filePath, true);
    if(loaded != OpenVideoResult::Success)
        return summary;

    SwitchDecodingMode(VideoDecodingMode::OnDemand);

    summary->IsImage = m_VideoInfo.DurationTimeStamps == 1;
    summary->DurationMilliseconds = (int64_t)(((m_VideoInfo.DurationTimeStamps - m_VideoInfo.AverageTimeStampsPerFrame) / m_VideoInfo.AverageTimeStampsPerSeconds) * 1000.0);
    summary->ImageSize = m_VideoInfo.OriginalSize;
    summary->Framerate = m_VideoInfo.FramesPerSeconds;


    // Read some frames (directly decode at small size).
    float stretch = (float)m_VideoInfo.OriginalSize.Width / _maxSize.Width;
    m_DecodingSize = Size(_maxSize.Width, (int)(m_VideoInfo.OriginalSize.Height / stretch));

    int64_t step = (int64_t)Math::Ceiling(m_VideoInfo.DurationTimeStamps / (double)_thumbs);
    int64_t previousFrameTimestamp = -1;

    for(int64_t ts = 0; ts < m_VideoInfo.DurationTimeStamps; ts += step)
    {
        ReadResult read = ReadResult::FrameNotRead;
        if(ts == 0)
            read = ReadFrame(-1, 1, true);
        else
            read = ReadFrame(ts, 1, true);

        if (read == ReadResult::Success && 
            m_FramesContainer->CurrentFrame != nullptr &&
            m_TimestampInfo.CurrentTimestamp > previousFrameTimestamp)
        {
            Bitmap^ bmp = Extensions::CloneDeep(m_FramesContainer->CurrentFrame->Image);
            summary->Thumbs->Add(bmp);
            previousFrameTimestamp = m_TimestampInfo.CurrentTimestamp;
        }
        else
        {
            break;
        }
    }
    
    Close();
    return summary;
}
void VideoReaderFFMpeg::PostLoad()
{
    if(CanPreBuffer && m_DecodingMode == VideoDecodingMode::OnDemand)
    {
        SwitchDecodingMode(VideoDecodingMode::PreBuffering);
        
        // FIXME: use a spin loop in the caller instead of sleeping.

        // Add a small temporisation so the prebuffering thread can decode the first frame.
        // The UI thread will very soon ask for the first frame of the working zone, 
        // if it's too quick we would cancel the thread at the same time it decodes the request frame.
        //Thread::CurrentThread->Sleep(40);
        Thread::CurrentThread->Sleep(100);
    }
}
bool VideoReaderFFMpeg::MoveNext(int _skip, bool _decodeIfNecessary)
{
    if(!m_bIsLoaded || m_DecodingMode == VideoDecodingMode::NotInitialized)
        return false;

    bool moved = false;

    if(m_DecodingMode == VideoDecodingMode::OnDemand)
    {
        ReadResult res = ReadFrame(-1, _skip + 1, false);
        moved = res == ReadResult::Success;
    }
    else if(m_DecodingMode == VideoDecodingMode::Caching)
    {
        moved = m_Cache->MoveBy(_skip + 1);
    }
    else if(m_DecodingMode == VideoDecodingMode::PreBuffering)
    {
        if(!_decodeIfNecessary || m_PreBuffer->HasNext(_skip))
        {
            m_PreBuffer->MoveBy(_skip + 1);
            moved = true;
        }
        else
        {
            // Stop thread, decode frame, move to it, restart thread.
            StopPreBuffering();
            ReadResult res = ReadFrame(-1, _skip + 1, false);
            if(res == ReadResult::Success)
                moved = m_PreBuffer->MoveBy(_skip + 1);
            StartPreBuffering();
        }
    }
    
    return moved && HasMoreFrames();
}
bool VideoReaderFFMpeg::MoveTo(int64_t _timestamp)
{
    if(!m_bIsLoaded || m_DecodingMode == VideoDecodingMode::NotInitialized)
        return false;

    bool moved = false;

    if(m_DecodingMode == VideoDecodingMode::OnDemand)
    {
        ReadResult res = ReadFrame(_timestamp, 1, false);
        moved = (res == ReadResult::Success);
    }
    else if(m_DecodingMode == VideoDecodingMode::Caching)
    {
        moved = m_Cache->MoveTo(_timestamp);
    }
    else if(m_DecodingMode == VideoDecodingMode::PreBuffering)
    {
        //log->DebugFormat("MoveTo [{0}]", _timestamp);
        if(m_PreBuffer->Contains(_timestamp))
        {
            moved = m_PreBuffer->MoveTo(_timestamp);
        }
        else
        {
            // Stop thread, decode frame, move to it, restart thread.
            StopPreBuffering();

            // Adding the target frame will either keep the prebuffer frames contiguous or not.
            // If the frame is the next one or it's a rollover jump, fine. Otherwise we need to clear.
            // jump to next frame after current segment is currently not handled gracefully and will clear anyway.
            // (Avoids another locking just for a very rare case).
            if(!m_PreBuffer->IsRolloverJump(_timestamp))
            {
                log->DebugFormat("Out of segment jump, clearing cache. Asked {0} in {1}.", _timestamp, m_PreBuffer->Segment);
                m_PreBuffer->Clear();
            }
            
            // This is done on the UI thread but the decoding thread has just been put to sleep.
            ReadResult res = ReadFrame(_timestamp, 1, false);
            
            if(res == ReadResult::Success)
            {
                // The actual timestamp we land on might not be the one requested, due to pixel to timestamp interpolation.
                int64_t actualTarget = m_TimestampInfo.CurrentTimestamp;
                moved = m_PreBuffer->MoveTo(actualTarget);
            }

            StartPreBuffering();
        }
    }

    return moved && HasMoreFrames();
}
String^ VideoReaderFFMpeg::ReadMetadata()
{
    if(m_iMetadataStream < 0)
        return "";
    
    String^ metadata = "";
    bool done = false;
    do
    {
        AVPacket InputPacket;
        if((av_read_frame( m_pFormatCtx, &InputPacket)) < 0)
            break;
        
        if(InputPacket.stream_index != m_iMetadataStream)
            continue;

        metadata = gcnew String((char*)InputPacket.data);
        done = true;
    }
    while(!done);
    
    // Back to start.
    avformat_seek_file(m_pFormatCtx, m_iVideoStream, 0, 0, 0, AVSEEK_FLAG_BACKWARD); 
    
    return metadata;
}

bool VideoReaderFFMpeg::ChangeAspectRatio(ImageAspectRatio _ratio)
{
    if(!CanChangeAspectRatio)
        throw gcnew CapabilityNotSupportedException();

    // Decoding thread should be stopped at this point.
    if(m_PreBufferingThread != nullptr && m_PreBufferingThread->IsAlive)
        log->ErrorFormat("PreBuffering thread is started.");

    Options->ImageAspectRatio = _ratio;
    SetAspectRatioSize(_ratio);

    // TODO: decoding size should be updated from the outside ?
    m_DecodingSize = m_VideoInfo.AspectRatioSize;

    m_FramesContainer->Clear();
    return true;
}
bool VideoReaderFFMpeg::ChangeDeinterlace(bool _deint)
{
    if(!CanChangeDeinterlacing)
        throw gcnew CapabilityNotSupportedException();

    // Decoding thread should be stopped at this point.
    Options->Deinterlace = _deint;
    m_FramesContainer->Clear();
    return true;
}
void VideoReaderFFMpeg::ChangeDecodingSize(Size _size)
{
    if(!CanChangeDecodingSize)
        throw gcnew CapabilityNotSupportedException();
    
    if(m_DecodingMode != VideoDecodingMode::PreBuffering)
    {
        log->Debug("Will not change decoding size because we are not prebuffering.");
        m_CanDrawUnscaled = false;
        return;
    }
    
    Size targetSize = FixSize(_size);
    if(targetSize == m_DecodingSize)
    {
        log->DebugFormat("Already decoding at the right size.");
        m_CanDrawUnscaled = true;
        return;
    }

    log->DebugFormat("Changing decoding size from {0} to {1}", m_DecodingSize, targetSize);
    
    long currentTimestamp = m_PreBuffer->CurrentFrame != nullptr ?  m_PreBuffer->CurrentFrame->Timestamp : -1;

    StopPreBuffering();
    m_PreBuffer->Clear();
    m_DecodingSize = targetSize;
    m_CanDrawUnscaled = true;
    
    if(currentTimestamp >= 0)
    {
        ReadResult res = ReadFrame(currentTimestamp, 1, false);
        if(res == ReadResult::Success)
            m_PreBuffer->MoveTo(currentTimestamp);
    }

    StartPreBuffering();
}
void VideoReaderFFMpeg::DisableCustomDecodingSize()
{
    // This is used when the player is doing operations that are not compatible with rendering unscaled,
    // like tracking.
    m_CanDrawUnscaled = false;

    if(m_DecodingMode != VideoDecodingMode::PreBuffering)
        return;

    long currentTimestamp = m_PreBuffer->CurrentFrame != nullptr ?  m_PreBuffer->CurrentFrame->Timestamp : -1;

    StopPreBuffering();
    m_PreBuffer->Clear();
    ResetDecodingSize();

    if(currentTimestamp >= 0)
    {
        ReadResult res = ReadFrame(currentTimestamp, 1, false);
        if(res == ReadResult::Success)
            m_PreBuffer->MoveTo(currentTimestamp);
    }
    
    StartPreBuffering();
}
void VideoReaderFFMpeg::ResetDecodingSize()
{
    m_DecodingSize = m_VideoInfo.AspectRatioSize;
    m_CanDrawUnscaled = false;
}
bool VideoReaderFFMpeg::WorkingZoneFitsInMemory(VideoSection _newZone, int _maxSeconds, int _maxMemory)
{
    double durationSeconds = (double)(_newZone.End - _newZone.Start) / m_VideoInfo.AverageTimeStampsPerSeconds;

    // Loading is done at full aspect ratio size, not at the current decoding size based on the rendering container.
    // Otherwise we would have to potentially reload the cache each time there is a stretch/squeeze request.
    int64_t frameBytes = avpicture_get_size(m_PixelFormatFFmpeg, m_VideoInfo.AspectRatioSize.Width, m_VideoInfo.AspectRatioSize.Height);
    double frameMegaBytes = (double)frameBytes / 1048576;
    double durationMegaBytes = durationSeconds * m_VideoInfo.FramesPerSeconds * frameMegaBytes;
    
    return durationSeconds > 0 && durationSeconds <= _maxSeconds && durationMegaBytes <= _maxMemory;
}
void VideoReaderFFMpeg::SwitchDecodingMode(VideoDecodingMode _mode)
{
    if(_mode == m_DecodingMode)
        return;

    if(!CanSwitchDecodingMode(_mode))
        throw gcnew CapabilityNotSupportedException();

    log->DebugFormat("Switching decoding mode. {0} -> {1}", m_DecodingMode.ToString(), _mode.ToString());

    if(m_DecodingMode == VideoDecodingMode::PreBuffering)
    {
        StopPreBuffering();
        ResetDecodingSize();
    }

    if(m_FramesContainer != nullptr)
        m_FramesContainer->Clear();
    
    m_DecodingMode = _mode;
    switch(m_DecodingMode)
    {
    case VideoDecodingMode::OnDemand:
        m_FramesContainer = m_SingleFrameContainer;
        break;
    case VideoDecodingMode::PreBuffering:
        m_FramesContainer = m_PreBuffer;
        m_PreBuffer->UpdateWorkingZone(m_WorkingZone);
        SeekTo(m_WorkingZone.Start);
        StartPreBuffering();
        break;
    case VideoDecodingMode::Caching:
        
        m_FramesContainer = m_Cache;
        break;
    default:
        m_FramesContainer = nullptr;
    }
}
void VideoReaderFFMpeg::UpdateWorkingZone(VideoSection _newZone, bool _forceReload, int _maxSeconds, int _maxMemory, Action<DoWorkEventHandler^>^ _workerFn)
{
    if(!m_bIsLoaded || m_DecodingMode == VideoDecodingMode::NotInitialized)
        return;
    
    if(!CanChangeWorkingZone)
        throw gcnew CapabilityNotSupportedException();

    log->DebugFormat("Update working zone request. {0} to {1}. Force reload:{2}", m_WorkingZone, _newZone, _forceReload);

    if(!_forceReload && m_WorkingZone == _newZone)
        return;
    
    if(!CanCache)
    {
        m_WorkingZone = _newZone;
        if(m_DecodingMode == VideoDecodingMode::OnDemand && CanPreBuffer)
            SwitchDecodingMode(VideoDecodingMode::PreBuffering);
        else if (m_DecodingMode == VideoDecodingMode::PreBuffering)
            m_PreBuffer->UpdateWorkingZone(m_WorkingZone);
    }
    else
    {
        if(_workerFn == nullptr)
            throw gcnew ArgumentNullException("workerFn");
        
        // Try to (re)load the entire working zone in the cache.
        // We try not to load parts that are already loaded.

        // The new working zone requested may come from an interpolation between pixels and timestamps,
        // it is not guaranteed to land on exact frames. We must reupdate our internal value with
        // the actual boundaries, be it for reducing or expanding.
        
        log->DebugFormat("Working zone update. Current:{0}, Asked:{1}",m_WorkingZone, _newZone);
        
        if(!WorkingZoneFitsInMemory(_newZone, _maxSeconds, _maxMemory))
        {
            log->Debug("New working zone does not fit in memory.");
            m_WorkingZone = _newZone;
            SwitchToBestAfterCaching();
        }
        else
        {
            VideoSection sectionToCache = VideoSection::Empty;
            bool prepend = false;

            if(m_DecodingMode != VideoDecodingMode::Caching || _forceReload)
            {
                log->Debug("Just entering the cached mode, import everything.");
                SwitchDecodingMode(VideoDecodingMode::Caching);
                sectionToCache = _newZone;
            }
            else
            {
                // First reduce where needed, then expand.
                if(_newZone.Start > m_WorkingZone.Start)
                {
                    m_Cache->ReduceWorkingZone(VideoSection(_newZone.Start, m_WorkingZone.End));
                    m_WorkingZone = m_Cache->WorkingZone;
                }

                if(_newZone.End < m_WorkingZone.End)
                {
                    m_Cache->ReduceWorkingZone(VideoSection(m_WorkingZone.Start, _newZone.End));
                    m_WorkingZone = m_Cache->WorkingZone;
                }
                
                if(_newZone.Start < m_WorkingZone.Start && _newZone.End > m_WorkingZone.End)
                {
                    // Special case of both prepend and append. Clear all and import all for simplicity.
                    // Unfortunately this may also happen as the result of rounding error during pixel to timestamp conversion.
                    m_Cache->Clear();
                    sectionToCache = _newZone;
                }
                else if(_newZone.Start < m_WorkingZone.Start)
                {
                    // Prepending only.
                    sectionToCache = VideoSection(_newZone.Start, m_WorkingZone.Start);
                    prepend = true;
                }
                else
                {
                    // Appending only.
                    sectionToCache = VideoSection(m_WorkingZone.End, _newZone.End);
                }
            }

            if(!sectionToCache.IsEmpty)
            {
                log->DebugFormat("New frames to cache needed:{0}", sectionToCache);
                //SwitchDecodingMode(VideoDecodingMode::Caching);

                // As C++/CLI doesn't support lambdas expressions, we have to resort to a separate method and global variables.
                m_SectionToCache = sectionToCache;
                m_Prepend = prepend;
                DoWorkEventHandler^ workHandler = gcnew DoWorkEventHandler(this, &VideoReaderFFMpeg::ImportWorkingZoneToCache);
                _workerFn(workHandler);

                /*C# (including ImportWorkingZoneToCache)
                _workerFn((s,e) => {
                    bool success = ReadMany((BackgroundWorker)s, sectionToCache, prepend));
                    if(!success)
                        ExitCaching();
                }*/
            }
        }
    }
}
void VideoReaderFFMpeg::ImportWorkingZoneToCache(System::Object^ sender, DoWorkEventArgs^ e)
{
    BackgroundWorker^ worker = dynamic_cast<BackgroundWorker^>(sender);
    bool success = ReadMany(worker, m_SectionToCache, m_Prepend);
    m_SectionToCache = VideoSection::Empty;
    m_Prepend = false;

    if(!success)
        SwitchToBestAfterCaching();	
}
void VideoReaderFFMpeg::SwitchToBestAfterCaching()
{
    // If we cannot enter Caching mode, switch to the next best thing.
    if(CanPreBuffer)
        SwitchDecodingMode(VideoDecodingMode::PreBuffering);
    else if(CanDecodeOnDemand)
        SwitchDecodingMode(VideoDecodingMode::OnDemand);
    else 
        throw gcnew CapabilityNotSupportedException();
}
bool VideoReaderFFMpeg::ReadMany(BackgroundWorker^ _bgWorker, VideoSection _section, bool _prepend)
{
    // Load the asked section to cache (doesn't move the playhead).
    // Called when filling the cache with the Working Zone.
    // Might also be called internally when loading a very short video or single image.

    if(!CanCache || m_DecodingMode != VideoDecodingMode::Caching)
        throw gcnew CapabilityNotSupportedException("Importing to cache is not supported for the video.");

    if(_bgWorker != nullptr)
        Thread::CurrentThread->Name = "CacheFilling";
    
    log->DebugFormat("Caching section {0}, prepend:{1}", _section, _prepend);

    m_Cache->SetPrependBlock(_prepend);
    
    bool success = true;
    int read = 0;
    int total = (int)((_section.End - _section.Start + m_VideoInfo.AverageTimeStampsPerFrame)/m_VideoInfo.AverageTimeStampsPerFrame);
    
    ReadResult res;
    // If the video is very short this call can only happen when opening the video.
    // We avoid a useless seek in this case. Prevent problems with non seekable files like single images.
    if(m_bIsVeryShort)
        res = ReadFrame(-1, 1, false);
    else
        res = ReadFrame(_section.Start, 1, false);
    
    success = res == ReadResult::Success;
    while(m_TimestampInfo.CurrentTimestamp < _section.End && read < total && res == ReadResult::Success)
    {
        if(_bgWorker != nullptr && _bgWorker->CancellationPending)
        {
            log->DebugFormat("Cancellation at frame [{0}]", m_TimestampInfo.CurrentTimestamp);
            m_Cache->Clear();
            success = false;
            break;
        }
        
        res = ReadFrame(-1, 1, false);
        success = res == ReadResult::Success;
        
        if(_bgWorker != nullptr)
            _bgWorker->ReportProgress(read++, total);
    }

    m_Cache->SetPrependBlock(false);

    if(read >= total - 1)
        m_WorkingZone = m_Cache->WorkingZone;

    return success;
}
void VideoReaderFFMpeg::BeforeFrameEnumeration()
{
    // Frames are about to be enumerated (for example for saving).
    // This operation is not compatible with Prebuffering mode.
    if(m_DecodingMode == VideoDecodingMode::PreBuffering)
    {	
        m_WasPrebuffering = true;
        SwitchDecodingMode(VideoDecodingMode::OnDemand);
    }
}
void VideoReaderFFMpeg::AfterFrameEnumeration()
{
    if(m_WasPrebuffering)
        SwitchDecodingMode(VideoDecodingMode::PreBuffering);
    m_WasPrebuffering = false;
}
void VideoReaderFFMpeg::ResetDrops()
{
    if(m_DecodingMode == VideoDecodingMode::PreBuffering)
        m_PreBuffer->ResetDrops();
}
void VideoReaderFFMpeg::BeforePlayloop()
{
    // Just in case something wrong happened, make sure the decoding thread is alive.
    if(DecodingMode != VideoDecodingMode::Caching &&
        (CanPreBuffer && DecodingMode != VideoDecodingMode::PreBuffering))
    {
        log->Error("Forcing PreBuffering thread to restart.");
        SwitchDecodingMode(VideoDecodingMode::PreBuffering);
    }
}
void VideoReaderFFMpeg::StartPreBuffering()
{
    if(!CanPreBuffer)
        throw gcnew CapabilityNotSupportedException();

    if(m_DecodingMode == VideoDecodingMode::Caching)
        return;

    if(m_PreBufferingThread != nullptr && m_PreBufferingThread->IsAlive)
    {
        log->Error("Prebuffering thread already started");
        StopPreBuffering();
        m_PreBuffer->Clear();
        //debug - just to check when we could pass here.
        //throw gcnew CapabilityNotSupportedException();
    }

    log->Debug("Starting prebuffering thread.");
    ParameterizedThreadStart^ pts = gcnew ParameterizedThreadStart(this, &VideoReaderFFMpeg::PreBufferingWorker);
    m_PreBufferingThreadCanceler->Reset();
    m_PreBufferingThread = gcnew Thread(pts);
    m_PreBufferingThread->Start(m_PreBufferingThreadCanceler);
}
void VideoReaderFFMpeg::StopPreBuffering()
{
    if(m_PreBufferingThread == nullptr || !m_PreBufferingThread->IsAlive)
        return;

    log->Debug("Stopping prebuffering thread.");
    m_PreBufferingThreadCanceler->Cancel();

    // The cancellation will only be effective when we next pass in the 
    // decoding loop and check the cancellation flag. This means that if the thread is in waiting state, 
    // (trying to push a frame to an already full buffer), the cancellation will not proceed.
    // UnblockAndMakeRoom will force a Pulse, dequeing a frame if necessary.
    // However, if we just make room for one frame and it's the UI thread that is doing the Add,
    // it will be blocked after the addition since the buffer will again be full. 
    // We must actually make sure the next Read operation won't block.
    m_PreBuffer->UnblockAndMakeRoom();

    m_PreBufferingThread->Join();
}
OpenVideoResult VideoReaderFFMpeg::Load(String^ _filePath, bool _forSummary)
{
    OpenVideoResult result = OpenVideoResult::Success;

    if(m_bIsLoaded) 
        Close();

    m_VideoInfo.FilePath = _filePath;
    if(Options == nullptr)
        Options = Options->Default;
    
    do
    {
        // Open file and get info on format (muxer).
        AVFormatContext* pFormatCtx = nullptr;
        char* pszFilePath = static_cast<char *>(Marshal::StringToHGlobalAnsi(_filePath).ToPointer());
        if(avformat_open_input(&pFormatCtx, pszFilePath, NULL, NULL) != 0)
        {
            result = OpenVideoResult::FileNotOpenned;
            log->ErrorFormat("The file {0} could not be openned. (Wrong path or not a video/image.)", _filePath);
            break;
        }
        Marshal::FreeHGlobal(safe_cast<IntPtr>(pszFilePath));
        
        // Info on streams.
        if(avformat_find_stream_info(pFormatCtx, nullptr) < 0 )
        {
            result = OpenVideoResult::StreamInfoNotFound;
            log->Error("The streams Infos were not Found.");
            break;
        }
        
        // Check for muxed KVA.
        m_iMetadataStream = GetStreamIndex(pFormatCtx, AVMEDIA_TYPE_SUBTITLE);
        if(m_iMetadataStream >= 0)
        {
            AVDictionaryEntry* pMetadataTag = av_dict_get(pFormatCtx->streams[m_iMetadataStream]->metadata, "language", nullptr, 0);

            if(pFormatCtx->streams[m_iMetadataStream]->codec->codec_id == CODEC_ID_TEXT &&
                pMetadataTag != nullptr &&
                strcmp((char*)pMetadataTag->value, "XML") == 0)
            {
                m_VideoInfo.HasKva = true;
            }
            else
            {
                log->Debug("Subtitle stream found, but not analysis meta data: ignored.");
                m_iMetadataStream = -1;
            }
        }

        // Video stream.
        if( (m_iVideoStream = GetStreamIndex(pFormatCtx, AVMEDIA_TYPE_VIDEO)) < 0 )
        {
            result = OpenVideoResult::VideoStreamNotFound;
            log->Error("No Video stream found in the file. (File is audio only, or video stream is broken.)");
            break;
        }

        // Codec
        AVCodec* pCodec = nullptr;
        AVCodecContext* pCodecCtx = pFormatCtx->streams[m_iVideoStream]->codec;
        m_VideoInfo.IsCodecMpeg2 = (pCodecCtx->codec_id == CODEC_ID_MPEG2VIDEO);
        if( (pCodec = avcodec_find_decoder(pCodecCtx->codec_id)) == nullptr)
        {
            result = OpenVideoResult::CodecNotFound;
            log->Error("No suitable codec to decode the video. (Worse than an unsupported codec.)");
            break;
        }

        if(avcodec_open2(pCodecCtx, pCodec, nullptr) < 0)
        {
            result = OpenVideoResult::CodecNotOpened;
            log->Error("Codec could not be openned. (Codec known, but not supported yet.)");
            break;
        }

        // The fundamental unit of time in Kinovea is the timebase of the file.
        // The timebase is the unit of time (in seconds) in which the timestamps are represented.
        m_VideoInfo.AverageTimeStampsPerSeconds = (double)pFormatCtx->streams[m_iVideoStream]->time_base.den / (double)pFormatCtx->streams[m_iVideoStream]->time_base.num;
        double fAvgFrameRate = 0.0;
        if(pFormatCtx->streams[m_iVideoStream]->avg_frame_rate.den != 0)
            fAvgFrameRate = (double)pFormatCtx->streams[m_iVideoStream]->avg_frame_rate.num / (double)pFormatCtx->streams[m_iVideoStream]->avg_frame_rate.den;

        // This may be updated after the first actual decoding.
        if(pFormatCtx->start_time > 0)
            m_VideoInfo.FirstTimeStamp = (int64_t)((double)((double)pFormatCtx->start_time / (double)AV_TIME_BASE) * m_VideoInfo.AverageTimeStampsPerSeconds);
        else
            m_VideoInfo.FirstTimeStamp = 0;
    
        if(pFormatCtx->duration > 0)
            m_VideoInfo.DurationTimeStamps = (int64_t)((double)((double)pFormatCtx->duration/(double)AV_TIME_BASE)*m_VideoInfo.AverageTimeStampsPerSeconds);
        else
            m_VideoInfo.DurationTimeStamps = 0;

        if(m_VideoInfo.DurationTimeStamps <= 0)
        {
            result = OpenVideoResult::StreamInfoNotFound;
            log->Error("Duration info not found.");
            break;
        }
        
        // Average FPS. Based on the following sources:
        // - libav in stream info (already in fAvgFrameRate).
        // - libav in container or stream with duration in frames or microseconds (Rarely available but valid if so).
        // - stream->time_base	(Often KO, like 90000:1, expresses the timestamps unit)
        // - codec->time_base (Often OK, but not always).
        // - some ad-hoc special cases.
        int iTicksPerFrame = pCodecCtx->ticks_per_frame;
        m_VideoInfo.FramesPerSeconds = 0;
        if(fAvgFrameRate != 0)
        {
            m_VideoInfo.FramesPerSeconds = fAvgFrameRate;
            log->Debug("Average Fps estimation method: libav.");
        }
        else
        {
            // 1.a. Durations
            if( (pFormatCtx->streams[m_iVideoStream]->nb_frames > 0) && (pFormatCtx->duration > 0))
            {	
                m_VideoInfo.FramesPerSeconds = ((double)pFormatCtx->streams[m_iVideoStream]->nb_frames * (double)AV_TIME_BASE)/(double)pFormatCtx->duration;

                if(iTicksPerFrame > 1)
                    m_VideoInfo.FramesPerSeconds /= iTicksPerFrame;
                
                log->Debug("Average Fps estimation method: Durations.");
            }
            else
            {
                // 1.b. stream->time_base, consider invalid if >= 1000.
                m_VideoInfo.FramesPerSeconds = (double)pFormatCtx->streams[m_iVideoStream]->time_base.den / (double)pFormatCtx->streams[m_iVideoStream]->time_base.num;
                
                if(m_VideoInfo.FramesPerSeconds < 1000)
                {
                    if(iTicksPerFrame > 1)
                        m_VideoInfo.FramesPerSeconds /= iTicksPerFrame;		

                    log->Debug("Average Fps estimation method: Stream timebase.");
                }
                else
                {
                    // 1.c. codec->time_base, consider invalid if >= 1000.
                    m_VideoInfo.FramesPerSeconds = (double)pCodecCtx->time_base.den / (double)pCodecCtx->time_base.num;

                    if(m_VideoInfo.FramesPerSeconds < 1000)
                    {
                        if(iTicksPerFrame > 1)
                            m_VideoInfo.FramesPerSeconds /= iTicksPerFrame;
                        
                        log->Debug("Average Fps estimation method: Codec timebase.");
                    }
                    else if (m_VideoInfo.FramesPerSeconds == 30000)
                    {
                        m_VideoInfo.FramesPerSeconds = 29.97;
                        log->Debug("Average Fps estimation method: special case detection (30000:1 -> 30000:1001).");
                    }
                    else if (m_VideoInfo.FramesPerSeconds == 25000)
                    {
                        m_VideoInfo.FramesPerSeconds = 24.975;
                        log->Debug("Average Fps estimation method: special case detection (25000:1 -> 25000:1001).");
                    }
                    else
                    {
                        // Detection failed. Force to 25fps.
                        m_VideoInfo.FramesPerSeconds = 25;
                        log->Debug("Average Fps estimation method: Estimation failed. Fps will be forced to : " + m_VideoInfo.FramesPerSeconds);
                    }
                }
            }
        }
        log->Debug("Ticks per frame: " + iTicksPerFrame);

        m_VideoInfo.FrameIntervalMilliseconds = (double)1000/m_VideoInfo.FramesPerSeconds;
        m_VideoInfo.AverageTimeStampsPerFrame = (int64_t)Math::Round(m_VideoInfo.AverageTimeStampsPerSeconds / m_VideoInfo.FramesPerSeconds);
        
        m_WorkingZone = VideoSection(
            m_VideoInfo.FirstTimeStamp, 
            m_VideoInfo.FirstTimeStamp + m_VideoInfo.DurationTimeStamps - m_VideoInfo.AverageTimeStampsPerFrame);

        // Image size
        m_VideoInfo.OriginalSize = Size(pCodecCtx->width, pCodecCtx->height);
        
        if(pCodecCtx->sample_aspect_ratio.num != 0 && pCodecCtx->sample_aspect_ratio.num != pCodecCtx->sample_aspect_ratio.den)
        {
            // Anamorphic video, non square pixels.
            log->Debug("Display Aspect Ratio type: Anamorphic");
            if(pCodecCtx->codec_id == CODEC_ID_MPEG2VIDEO)
            {
                // If MPEG, sample_aspect_ratio is actually the DAR...
                // Reference for weird decision tree: mpeg12.c at mpeg_decode_postinit().
                double fDisplayAspectRatio = (double)pCodecCtx->sample_aspect_ratio.num / (double)pCodecCtx->sample_aspect_ratio.den;
                m_VideoInfo.PixelAspectRatio = ((double)pCodecCtx->height * fDisplayAspectRatio) / (double)pCodecCtx->width;

                if(m_VideoInfo.PixelAspectRatio < 1.0f)
                    m_VideoInfo.PixelAspectRatio = fDisplayAspectRatio;
            }
            else
            {
                m_VideoInfo.PixelAspectRatio = (double)pCodecCtx->sample_aspect_ratio.num / (double)pCodecCtx->sample_aspect_ratio.den;
            }	
            
            m_VideoInfo.SampleAspectRatio = Fraction(pCodecCtx->sample_aspect_ratio.num, pCodecCtx->sample_aspect_ratio.den);
        }
        else
        {
            // Assume PAR=1:1.
            log->Debug("Display Aspect Ratio type: Square Pixels");
            m_VideoInfo.PixelAspectRatio = 1.0f;
        }

        SetAspectRatioSize(Options->ImageAspectRatio);
        m_DecodingSize = m_VideoInfo.AspectRatioSize;
        
        m_pFormatCtx = pFormatCtx;
        m_pCodecCtx	= pCodecCtx;

        m_bIsLoaded = true;
        
        // If not many frames compared to the dynamic cache size (single image or very short video), 
        // load everything right away, freeze the cache, and disable extra capabilities.
        // the Cache.WorkingZone boundaries may be updated with actual values from the file.
        double nbFrames = (double)(m_VideoInfo.DurationTimeStamps / m_VideoInfo.AverageTimeStampsPerFrame);
        int veryShortThresholdFrames = 50;
        m_bIsVeryShort = nbFrames <= veryShortThresholdFrames;
        
        if(_forSummary)
        {
            m_Capabilities = VideoCapabilities::CanDecodeOnDemand;
            SwitchDecodingMode(VideoDecodingMode::OnDemand);
        }
        else if(m_bIsVeryShort)
        {
            m_Capabilities = VideoCapabilities::CanCache;
            SwitchDecodingMode(VideoDecodingMode::Caching);
            ReadMany(nullptr, m_WorkingZone, false);
        }
        else
        {
            m_Capabilities = VideoCapabilities::CanDecodeOnDemand | VideoCapabilities::CanPreBuffer | VideoCapabilities::CanCache;
            m_Capabilities = m_Capabilities | VideoCapabilities::CanChangeWorkingZone | VideoCapabilities::CanChangeAspectRatio | VideoCapabilities::CanChangeDeinterlacing;
            m_Capabilities = m_Capabilities | VideoCapabilities::CanChangeDecodingSize;
            SwitchDecodingMode(VideoDecodingMode::OnDemand);
        }

        result = OpenVideoResult::Success;
    }
    while(false);
    
    return result;
}
int VideoReaderFFMpeg::GetStreamIndex(AVFormatContext* _pFormatCtx, int _iCodecType)
{
    // Returns the best candidate stream for the specified type, -1 if not found.
    unsigned int iCurrentStreamIndex = -1;
    unsigned int iBestStreamIndex = -1;
    int64_t iBestFrames = -1;

    do
    {
        iCurrentStreamIndex++;
        if(_pFormatCtx->streams[iCurrentStreamIndex]->codec->codec_type != _iCodecType)
            continue;
        
        int64_t frames = _pFormatCtx->streams[iCurrentStreamIndex]->nb_frames;
        if(frames > iBestFrames)
        {
            iBestFrames = frames;
            iBestStreamIndex = iCurrentStreamIndex;
        }
    }
    while(iCurrentStreamIndex < _pFormatCtx->nb_streams-1);

    return (int)iBestStreamIndex;
}
void VideoReaderFFMpeg::SetAspectRatioSize(Kinovea::Video::ImageAspectRatio _ratio)
{
    // Set the image geometry according to the pixel aspect ratio choosen.
    log->DebugFormat("Image aspect ratio: {0}", _ratio);
    
    // Constraint width and change height to match aspect ratio.
    m_VideoInfo.AspectRatioSize.Width = m_VideoInfo.OriginalSize.Width;

    switch(_ratio)
    {
    case Kinovea::Video::ImageAspectRatio::Force43:
        m_VideoInfo.AspectRatioSize.Height = (int)((m_VideoInfo.OriginalSize.Width * 3.0) / 4.0);
        break;
    case Kinovea::Video::ImageAspectRatio::Force169:
        m_VideoInfo.AspectRatioSize.Height = (int)((m_VideoInfo.OriginalSize.Width * 9.0) / 16.0);
        break;
    case Kinovea::Video::ImageAspectRatio::ForcedSquarePixels:
        m_VideoInfo.AspectRatioSize.Height = m_VideoInfo.OriginalSize.Height;
        break;
    case Kinovea::Video::ImageAspectRatio::Auto:
    default:
        m_VideoInfo.AspectRatioSize.Height = (int)((double)m_VideoInfo.OriginalSize.Height / m_VideoInfo.PixelAspectRatio);
        break;
    }
    
    m_VideoInfo.AspectRatioSize = FixSize(m_VideoInfo.AspectRatioSize);

    if(m_VideoInfo.AspectRatioSize != m_VideoInfo.OriginalSize)
        log->DebugFormat("Image size: Original:{0}, AspectRatioSize:{1}", m_VideoInfo.OriginalSize, m_VideoInfo.AspectRatioSize);
}
Size VideoReaderFFMpeg::FixSize(Size _size)
{
    // Fix unsupported width for conversion to .NET Bitmap. Must be a multiple of 4.
    return Size(_size.Width + (_size.Width % 4), _size.Height);
}
ReadResult VideoReaderFFMpeg::ReadFrame(int64_t _iTimeStampToSeekTo, int _iFramesToDecode, bool _approximate)
{
    //------------------------------------------------------------------------------------
    // Reads a frame and adds it to the frame cache.
    // This function works either for MoveTo or MoveNext type of requests.
    // It decodes as many frames as needed to reach the target timestamp 
    // or the number of frames to decode. Seeks backwards if needed.
    //
    // The _approximate flag is used for thumbnails retrieval. 
    // In this case we don't really care to land exactly on the right frame,
    // so we return after the first decode post-seek.
    //------------------------------------------------------------------------------------
    
    m_LoopWatcher->LoopStart();
        
    // TODO: shouldn't need to lock. Make sure we don't synchronously ask for a frame while prebuffering.
    lock l(m_Locker);

    if(!m_bIsLoaded || m_DecodingMode == VideoDecodingMode::NotInitialized) 
        return ReadResult::MovieNotLoaded;

    if(m_FramesContainer == nullptr)
        return ReadResult::FrameContainerNotSet;

    ReadResult result = ReadResult::Success;
    int	iFramesToDecode = _iFramesToDecode;
    int64_t iTargetTimeStamp = _iTimeStampToSeekTo;
    bool seeking = false;

    // Find the proper target and number of frames to decode.
    if(_iFramesToDecode < 0)
    {
        // Negative move. Compute seek target.
        iTargetTimeStamp = m_FramesContainer->CurrentFrame->Timestamp + (_iFramesToDecode * m_VideoInfo.AverageTimeStampsPerFrame);
        if(iTargetTimeStamp < 0)
            iTargetTimeStamp = 0;
    }
     
    if(iTargetTimeStamp >= 0)
    {	
        seeking = true;
        iFramesToDecode = 1; // We'll use the target timestamp anyway.
        int iSeekRes = SeekTo(iTargetTimeStamp);
        if(iSeekRes < 0)
            log->ErrorFormat("Error during seek: {0}. Target was:[{1}]", iSeekRes, iTargetTimeStamp);
    }

    // Allocate 2 AVFrames, one for the raw decoded frame and one for deinterlaced/rescaled/converted frame.
    AVFrame* pDecodingAVFrame = av_frame_alloc();
    AVFrame* pFinalAVFrame = av_frame_alloc();

    // The buffer holding the actual frame data.
    int iSizeBuffer = avpicture_get_size(m_PixelFormatFFmpeg, m_DecodingSize.Width, m_DecodingSize.Height);
    uint8_t* pBuffer = iSizeBuffer > 0 ? new uint8_t[iSizeBuffer] : nullptr;

    if(pDecodingAVFrame == nullptr || pFinalAVFrame == nullptr || pBuffer == nullptr)
        return ReadResult::MemoryNotAllocated;

    // Assigns appropriate parts of buffer to image planes in the AVFrame.
    avpicture_fill((AVPicture *)pFinalAVFrame, pBuffer , m_PixelFormatFFmpeg, m_DecodingSize.Width, m_DecodingSize.Height);

    m_TimestampInfo.CurrentTimestamp = m_FramesContainer->CurrentFrame == nullptr ? -1 : m_FramesContainer->CurrentFrame->Timestamp;
    
    // Reading/Decoding loop
    bool done = false;
    bool bFirstPass = true;
    int iReadFrameResult;
    int iFrameFinished = 0;
    int	iFramesDecoded	= 0;
    do
    {
        // FFMpeg also has an internal buffer to cope with B-Frames entanglement.
        // The DTS/PTS announced is actually the one of the last frame that was put in the buffer by av_read_frame,
        // it is *not* the one of the frame that was extracted from the buffer by avcodec_decode_video.
        // To solve the DTS/PTS issue, we save the timestamps each time we find libav is buffering a frame.
        // And we use the previously saved timestamps.
        // Ref: http://lists.mplayerhq.hu/pipermail/libav-user/2008-August/001069.html

        // Read next packet
        AVPacket InputPacket;
        iReadFrameResult = av_read_frame( m_pFormatCtx, &InputPacket);

        if(iReadFrameResult < 0)
        {
            // Reading error. We don't know if the error happened on a video frame or audio one.
            done = true;
            delete [] pBuffer;
            result = ReadResult::FrameNotRead;
            break;
        }

        if(InputPacket.stream_index != m_iVideoStream)
        {
            av_free_packet(&InputPacket);
            continue;
        }

        // Decode video packet. This is needed even if we're not on the final frame yet.
        // I-Frame data is kept internally by ffmpeg and will need it to build the final frame.
        avcodec_decode_video2(m_pCodecCtx, pDecodingAVFrame, &iFrameFinished, &InputPacket);
        
        if(iFrameFinished == 0)
        {
            // Buffering frame. libav just read a I or P frame that will be presented later.
            // (But which was necessary to get now in order to decode a coming B frame.)
            SetTimestampFromPacket(InputPacket.dts, InputPacket.pts, false);
            av_free_packet(&InputPacket);
            continue;
        }

        // Update positions.
        SetTimestampFromPacket(InputPacket.dts, InputPacket.pts, true);

        if(seeking && bFirstPass && !_approximate && iTargetTimeStamp >= 0 && m_TimestampInfo.CurrentTimestamp > iTargetTimeStamp)
        {
            // If the current ts is already after the target, we are dealing with this kind of files
            // where the seek doesn't work as advertised. We'll seek back again further,
            // and then decode until we get to it.
            
            // Do this only once.
            bFirstPass = false;
            
            // For some files, one additional second back is not enough. The seek is wrong by up to 4 seconds.
            // We also allow the target to go before 0.
            int iSecondsBack = 4;
            int64_t iForceSeekTimestamp = iTargetTimeStamp - ((int64_t)m_VideoInfo.AverageTimeStampsPerSeconds * iSecondsBack);
            int64_t iMinTarget = System::Math::Min(iForceSeekTimestamp, (int64_t)0);
            
            // Do the seek.
            log->DebugFormat("[Seek] - First decoded frame [{0}] already after target [{1}]. Force seek {2} more seconds back to [{3}]", 
                            m_TimestampInfo.CurrentTimestamp, iTargetTimeStamp, iSecondsBack, iForceSeekTimestamp);
            
            avformat_seek_file(m_pFormatCtx, m_iVideoStream, iMinTarget , iForceSeekTimestamp, iForceSeekTimestamp, AVSEEK_FLAG_BACKWARD); 
            avcodec_flush_buffers(m_pFormatCtx->streams[m_iVideoStream]->codec);

            // Free the packet that was allocated by av_read_frame
            av_free_packet(&InputPacket);

            // Loop back to restart decoding frames until we get to the target.
            continue;
        }

        bFirstPass = false;
        iFramesDecoded++;

        //-------------------------------------------------------------------------------
        // If we're done, convert the image and store it into its final recipient.
        // - seek: if we reached the target timestamp.
        // - linear decoding: if we decoded the required number of frames.
        //-------------------------------------------------------------------------------
        if(	seeking && m_TimestampInfo.CurrentTimestamp >= iTargetTimeStamp ||
            !seeking && iFramesDecoded >= iFramesToDecode ||
            _approximate)
        {
            done = true;

            if(seeking && m_TimestampInfo.CurrentTimestamp != iTargetTimeStamp)
                log->DebugFormat("Seeking to [{0}] completed. Final position:[{1}]", iTargetTimeStamp, m_TimestampInfo.CurrentTimestamp);

            // Deinterlace + rescale + convert pixel format.
            bool rescaled = RescaleAndConvert(
                pFinalAVFrame, 
                pDecodingAVFrame, 
                m_DecodingSize.Width, 
                m_DecodingSize.Height, 
                m_PixelFormatFFmpeg,
                Options->Deinterlace);
            
            if(!rescaled)
            {
                delete [] pBuffer;
                result = ReadResult::ImageNotConverted;
                break;
            }
            
            try
            {
                // Import ffmpeg buffer into a .NET bitmap.
                int imageStride = pFinalAVFrame->linesize[0];
                IntPtr scan0 = IntPtr((void*)pFinalAVFrame->data[0]); 
                Bitmap^ bmp = gcnew Bitmap(m_DecodingSize.Width, m_DecodingSize.Height, imageStride, DecodingPixelFormat, scan0);

                // Store a pointer to the native buffer inside the Bitmap.
                // We'll be asked to free this resource later when the frame is not used anymore.
                // It is boxed inside an Object so we can extract it in a type-safe way.
                IntPtr^ boxedPtr = gcnew IntPtr((void*)pBuffer);
                bmp->Tag = boxedPtr;
                
                // Construct the VideoFrame and push it to the current container.
                VideoFrame^ vf = gcnew VideoFrame();
                vf->Image = bmp;
                vf->Timestamp = m_TimestampInfo.CurrentTimestamp;
                //log->DebugFormat("Pushing frame {0} to container. {1}", vf->Timestamp, m_DecodingMode);
                m_LoopWatcher->LoopEnd();
                m_FramesContainer->Add(vf);
            }
            catch(Exception^ exp)
            {
                delete [] pBuffer;
                result = ReadResult::ImageNotConverted;
                log->Error("Error while converting AVFrame to Bitmap.");
                log->Error(exp);
            }
        }
        
        // Free the packet that was allocated by av_read_frame
        av_free_packet(&InputPacket);
    }
    while(!done);
    
    // Free the AVFrames. (This will not deallocate the data buffers).
    av_free(pFinalAVFrame);
    av_free(pDecodingAVFrame);

#ifdef INSTRUMENTATION	
    if(m_FramesContainer->Current != nullptr)
        log->DebugFormat("[{0}] - Memory: {1:0,0} bytes", m_PreBuffer->CurrentFrame->Timestamp, Process::GetCurrentProcess()->PrivateMemorySize64);
#endif

    if (!m_bFirstFrameRead)
    {
        m_bFirstFrameRead = true;
        m_VideoInfo.FirstTimeStamp = m_TimestampInfo.CurrentTimestamp;
        m_WorkingZone = VideoSection(m_VideoInfo.FirstTimeStamp, m_WorkingZone.End);
    }

    return result;
}
int VideoReaderFFMpeg::SeekTo(int64_t _target)
{
    // Perform an FFMpeg seek without decoding the frame.
    // AVSEEK_FLAG_BACKWARD -> goes to first I-Frame before target.
    // Then we'll need to decode frame by frame until the target is reached.
    int res = avformat_seek_file(
        m_pFormatCtx, 
        m_iVideoStream, 
        0, 
        _target, 
        _target + (int64_t)m_VideoInfo.AverageTimeStampsPerSeconds,
        AVSEEK_FLAG_BACKWARD);
        
    avcodec_flush_buffers( m_pFormatCtx->streams[m_iVideoStream]->codec);
    m_TimestampInfo = TimestampInfo::Empty;
    return res;
}
void VideoReaderFFMpeg::SetTimestampFromPacket(int64_t _dts, int64_t _pts, bool _bDecoded)
{
    //---------------------------------------------------------------------------------------------------------
    // Try to guess the presentation timestamp of the packet we just read / decoded.
    // Presentation timestamps will be used everywhere for seeking, positioning, time calculations, etc.
    //
    // dts: decoding timestamp, 
    // pts: presentation timestamp, 
    // decoded: if libav finished to decode the frame or is just buffering.
    //
    // It must be noted that the timestamp given by libav is the timestamp of the frame it just read,
    // but the frame we will get from av_decode_video may come from its internal buffer and have a different timestamp.
    // Furthermore, some muxers do not fill the PTS value, and others only intermittently.
    // Kinovea prior to version 0.8.8 was using the DTS value as primary timestamp, which is wrong.
    //---------------------------------------------------------------------------------------------------------

    if(_pts == AV_NOPTS_VALUE || _pts < 0)
    {
        // Hum, too bad, the muxer did not specify the PTS for this packet.

        if(_bDecoded)
        {
            if(_dts == AV_NOPTS_VALUE || _dts < 0)
            {
                /*log->Debug(String::Format("Decoded - No value for PTS / DTS. Last known timestamp: {0}, Buffered ts if any: {1}", 
                    (m_PrimarySelection->iLastDecodedPTS >= 0)?String::Format("{0}", m_PrimarySelection->iLastDecodedPTS):"None", 
                    (m_PrimarySelection->iBufferedPTS < Int64::MaxValue)?String::Format("{0}", m_PrimarySelection->iBufferedPTS):"None"));*/

                if(m_TimestampInfo.BufferedPTS < Int64::MaxValue)
                {
                    // No info but we know a frame was previously buffered, so it must be this one we took out.
                    // Unfortunately, we don't know the timestamp of the frame that is being buffered now...
                    m_TimestampInfo.CurrentTimestamp = m_TimestampInfo.BufferedPTS;
                    m_TimestampInfo.BufferedPTS = Int64::MaxValue;
                }
                else if(m_TimestampInfo.LastDecodedPTS >= 0)
                {
                    // No info but we know a frame was previously decoded, so it must be shortly after it.
                    m_TimestampInfo.CurrentTimestamp = m_TimestampInfo.LastDecodedPTS + m_VideoInfo.AverageTimeStampsPerFrame;
                    //log->Debug(String::Format("Current PTS estimation: {0}",	m_PrimarySelection->iCurrentTimeStamp));
                }
                else
                {
                    // No info + never buffered, never decoded. This must be the first frame.
                    m_TimestampInfo.CurrentTimestamp = 0;
                    //log->Debug(String::Format("Setting current PTS to 0"));
                }
            }
            else
            {
                // DTS is given, but not PTS.
                // Either this file is not using PTS, or it just didn't fill it for this frame, no way to know...
                if(m_TimestampInfo.BufferedPTS < _dts)
                {
                    // Argh. Comparing buffered frame PTS with read frame DTS ?
                    // May work for files that only store DTS all along though.
                    //log->Debug(String::Format("Decoded buffered frame - [{0}]", m_PrimarySelection->iBufferedPTS));
                    //log->Debug(String::Format("Buffering - DTS:[{0}] - No PTS", _dts));
                    m_TimestampInfo.CurrentTimestamp = m_TimestampInfo.BufferedPTS;	
                    m_TimestampInfo.BufferedPTS = _dts;
                }
                else
                {
                    //log->Debug(String::Format("Decoded (direct) - DTS:[{0}], No PTS", _dts));
                    m_TimestampInfo.CurrentTimestamp = System::Math::Max((int64_t)0, _dts);
                }
            }

            m_TimestampInfo.LastDecodedPTS = m_TimestampInfo.CurrentTimestamp;
        }
        else
        {
            // Buffering a frame.
            // What if there is already something in the buffer ?
            // We should keep a queue of buffered frames and serve them back in order.
            if(_dts < 0)
            { 
                //log->Debug(String::Format("Buffering (no decode) - No PTS, negative DTS"));
                
                // Hopeless situation. Let's reset the buffered frame timestamp,
                // The decode will rely on the last seen PTS from a decoded frame, if any.
                m_TimestampInfo.BufferedPTS = Int64::MaxValue;
            }
            else if(_dts == AV_NOPTS_VALUE)
            {
                //log->Debug(String::Format("Buffering (no decode) - No PTS, No DTS"));
                m_TimestampInfo.BufferedPTS = 0;
            }
            else
            {
                //log->Debug(String::Format("Buffering (no decode) - No PTS, DTS:[{0}]", _dts));
                m_TimestampInfo.BufferedPTS = _dts;
            }
        }
    }
    else
    {
        // PTS is given (nice).
        // We still need to check if there is something in the buffer, in which case
        // the decoded frame is in fact the one from the buffer.
        // (This may not even hold true, for H.264 and out of GOP reference.)
        if(_bDecoded)
        {
            if(m_TimestampInfo.BufferedPTS < _pts)
            {
                // There is something in the buffer with a timestamp prior to the one of the decoded frame.
                // That is probably the frame we got from libav.
                // The timestamp it presented us on the other hand, is the one it's being buffering.
                //log->Debug(String::Format("Decoded buffered frame - PTS:[{0}]", m_PrimarySelection->iBufferedPTS));
                //log->Debug(String::Format("Buffering - DTS:[{0}], PTS:[{1}]", _dts, _pts));
                
                m_TimestampInfo.CurrentTimestamp = m_TimestampInfo.BufferedPTS;
                m_TimestampInfo.BufferedPTS = _pts;
            }
            else
            {
                //log->Debug(String::Format("Decoded (direct) - DTS:[{0}], PTS:[{1}]", _dts, _pts));
                m_TimestampInfo.CurrentTimestamp = _pts;
            }

            m_TimestampInfo.LastDecodedPTS = m_TimestampInfo.CurrentTimestamp;
        }
        else
        {
            // What if there is already something in the buffer ?
            // We should keep a queue of buffered frame and serve them back in order.
            //log->Debug(String::Format("Buffering (no decode) -- DTS:[{0}], PTS:[{1}]", _dts, _pts));
            m_TimestampInfo.BufferedPTS = _pts;
        }
    }
}
bool VideoReaderFFMpeg::RescaleAndConvert(AVFrame* _pOutputFrame, AVFrame* _pInputFrame, int _OutputWidth, int _OutputHeight, int _OutputFmt, bool _bDeinterlace)
{
    //------------------------------------------------------------------------
    // Function used by GetNextFrame.
    // Take the frame we just decoded and turn it to the right size/deint/fmt.
    // todo: sws_getContext could be done only once.
    //------------------------------------------------------------------------
    bool bSuccess = true;
    SwsContext* pSWSCtx = sws_getContext(
        m_pCodecCtx->width, 
        m_pCodecCtx->height, 
        m_pCodecCtx->pix_fmt, 
        _OutputWidth, 
        _OutputHeight, 
        (AVPixelFormat)_OutputFmt, 
        DecodingQuality, 
        nullptr, nullptr, nullptr); 
        
    uint8_t** ppOutputData = nullptr;
    int* piStride = nullptr;
    uint8_t* pDeinterlaceBuffer = nullptr;

    if(_bDeinterlace)
    {
        AVPicture*	pDeinterlacingFrame;
        AVPicture	tmpPicture;

        // Deinterlacing happens before resizing.
        int iSizeDeinterlaced = avpicture_get_size(m_pCodecCtx->pix_fmt, m_pCodecCtx->width, m_pCodecCtx->height);
        
        pDeinterlaceBuffer = new uint8_t[iSizeDeinterlaced];
        pDeinterlacingFrame = &tmpPicture;
        avpicture_fill(pDeinterlacingFrame, pDeinterlaceBuffer, m_pCodecCtx->pix_fmt, m_pCodecCtx->width, m_pCodecCtx->height);

        int resDeint = avpicture_deinterlace(pDeinterlacingFrame, (AVPicture*)_pInputFrame, m_pCodecCtx->pix_fmt, m_pCodecCtx->width, m_pCodecCtx->height);

        if(resDeint < 0)
        {
            // Deinterlacing failed, use original image.
            log->Debug("Deinterlacing failed, use original image.");
            //sws_scale(pSWSCtx, _pInputFrame->data, _pInputFrame->linesize, 0, m_pCodecCtx->height, _pOutputFrame->data, _pOutputFrame->linesize); 
            ppOutputData = _pInputFrame->data;
            piStride = _pInputFrame->linesize;
        }
        else
        {
            // Use deinterlaced image.
            //sws_scale(pSWSCtx, pDeinterlacingFrame->data, pDeinterlacingFrame->linesize, 0, m_pCodecCtx->height, _pOutputFrame->data, _pOutputFrame->linesize); 
            ppOutputData = pDeinterlacingFrame->data;
            piStride = pDeinterlacingFrame->linesize;
        }
    }
    else
    {
        //sws_scale(pSWSCtx, _pInputFrame->data, _pInputFrame->linesize, 0, m_pCodecCtx->height, _pOutputFrame->data, _pOutputFrame->linesize); 
        ppOutputData = _pInputFrame->data;
        piStride = _pInputFrame->linesize;
    }

    try
    {
        sws_scale(pSWSCtx, ppOutputData, piStride, 0, m_pCodecCtx->height, _pOutputFrame->data, _pOutputFrame->linesize); 
    }
    catch(Exception^)
    {
        bSuccess = false;
        log->Error("RescaleAndConvert Error : sws_scale failed.");
    }

    // Clean Up.
    sws_freeContext(pSWSCtx);
    
    if(pDeinterlaceBuffer != nullptr)
        delete [] pDeinterlaceBuffer;

    return bSuccess;
}
void VideoReaderFFMpeg::DisposeFrame(VideoFrame^ _frame)
{
    // Dispose the Bitmap and the native buffer.
    // The pointer to the native buffer was stored in the Tag property.
    IntPtr^ ptr = dynamic_cast<IntPtr^>(_frame->Image->Tag);
    delete _frame->Image;
    
    if(ptr != nullptr)
    {
        // Fixme: why is the delete [] taking more than 1ms ?
        uint8_t* pBuf = (uint8_t*)ptr->ToPointer();
        delete [] pBuf;
    }
}

void VideoReaderFFMpeg::PreBufferingWorker(Object^ _canceler)
{
    Thread::CurrentThread->Name = "PreBuffering";
    ThreadCanceler^ canceler = (ThreadCanceler^)_canceler;
    
    log->DebugFormat("PreBuffering thread started.");
    
    while(true)
    {
        if(canceler->CancellationPending)
        {
            log->DebugFormat("PreBuffering thread, cancellation detected.");
            break;
        }
        
        ReadResult res = ReadFrame(-1, 1, false);
        
        // Rollover.
        if(!canceler->CancellationPending && (res == ReadResult::FrameNotRead || m_TimestampInfo.CurrentTimestamp > m_WorkingZone.End))
        {
            log->DebugFormat("Average prebuffering loop time: {0:0.000}ms. (interval: {1:0.000}ms).", m_LoopWatcher->Average, m_VideoInfo.FrameIntervalMilliseconds);
            m_LoopWatcher->Restart();

            ReadFrame(m_WorkingZone.Start, 1, false);
        }
    }

    log->DebugFormat("Exiting PreBuffering thread.");
}
void VideoReaderFFMpeg::DumpInfo()
{
    log->Debug("---------------------------------------------------");
    log->Debug("[File] - Filename : " + Path::GetFileName(m_VideoInfo.FilePath));
    log->DebugFormat("[Container] - Name: {0} ({1})", gcnew String(m_pFormatCtx->iformat->name), gcnew String(m_pFormatCtx->iformat->long_name));
    DumpStreamsInfos(m_pFormatCtx);
    log->Debug("[Container] - Duration (s): " + (double)m_pFormatCtx->duration/1000000);
    log->Debug("[Container] - Bit rate: " + m_pFormatCtx->bit_rate);
    if(m_pFormatCtx->streams[m_iVideoStream]->nb_frames > 0)
        log->DebugFormat("[Stream] - Duration (frames): {0}", m_pFormatCtx->streams[m_iVideoStream]->nb_frames);
    else
        log->Debug("[Stream] - Duration (frames): Unavailable.");
    log->DebugFormat("[Stream] - PTS wrap bits: {0}", m_pFormatCtx->streams[m_iVideoStream]->pts_wrap_bits);
    log->DebugFormat("[Stream] - TimeBase: {0}:{1}", m_pFormatCtx->streams[m_iVideoStream]->time_base.den, m_pFormatCtx->streams[m_iVideoStream]->time_base.num);
    log->DebugFormat("[Stream] - Average timestamps per seconds: {0}", m_VideoInfo.AverageTimeStampsPerSeconds);
    log->DebugFormat("[Container] - Start time (µs): {0}", m_pFormatCtx->start_time);
    log->DebugFormat("[Container] - Start timestamp: {0}", m_VideoInfo.FirstTimeStamp);
    log->DebugFormat("[Codec] - Name: {0}, id:{1}", gcnew String(m_pCodecCtx->codec_name), (int)m_pCodecCtx->codec_id);
    log->DebugFormat("[Codec] - TimeBase: {0}:{1}", m_pCodecCtx->time_base.den, m_pCodecCtx->time_base.num);
    log->Debug("[Codec] - Bit rate: " + m_pCodecCtx->bit_rate);
    log->Debug("Duration in timestamps: " + m_VideoInfo.DurationTimeStamps);
    log->Debug("Duration in seconds (computed): " + (double)(double)m_VideoInfo.DurationTimeStamps/(double)m_VideoInfo.AverageTimeStampsPerSeconds);
    log->Debug("Average Fps: " + m_VideoInfo.FramesPerSeconds);
    log->Debug("Average Frame Interval (ms): " + m_VideoInfo.FrameIntervalMilliseconds);
    log->Debug("Average Timestamps per frame: " + m_VideoInfo.AverageTimeStampsPerFrame);
    log->DebugFormat("[Codec] - Has B Frames: {0}", m_pCodecCtx->has_b_frames);
    log->Debug("[Codec] - Width (pixels): " + m_pCodecCtx->width);
    log->Debug("[Codec] - Height (pixels): " + m_pCodecCtx->height);
    log->Debug("[Codec] - Pixel Aspect Ratio: " + m_VideoInfo.PixelAspectRatio);
    log->Debug("---------------------------------------------------");
}


void VideoReaderFFMpeg::DumpStreamsInfos(AVFormatContext* _pFormatCtx)
{
    log->Debug("[Container] - Number of streams: " + _pFormatCtx->nb_streams);
    
    for(int i = 0;i<(int)_pFormatCtx->nb_streams;i++)
    {
        String^ streamType;
        
        switch((int)_pFormatCtx->streams[i]->codec->codec_type)
        {
        case AVMEDIA_TYPE_VIDEO:
            streamType = "AVMEDIA_TYPE_VIDEO";
            break;
        case AVMEDIA_TYPE_AUDIO:
            streamType = "AVMEDIA_TYPE_AUDIO";
            break;
        case AVMEDIA_TYPE_DATA:
            streamType = "AVMEDIA_TYPE_DATA";
            break;
        case AVMEDIA_TYPE_SUBTITLE:
            streamType = "AVMEDIA_TYPE_SUBTITLE";
            break;
        case AVMEDIA_TYPE_UNKNOWN:
        default:
            streamType = "AVMEDIA_TYPE_UNKNOWN";
            break;
        }

        log->DebugFormat("[Stream] #{0}, Type : {1}, {2}", i, streamType, _pFormatCtx->streams[i]->nb_frames);
    }
}
void VideoReaderFFMpeg::DumpFrameType(int _type)
{
    switch(_type)
    {
    case AV_PICTURE_TYPE_I:
        log->Debug("(I) Frame +++++");
        break;
    case AV_PICTURE_TYPE_P:
        log->Debug("(P) Frame --");
        break;
    case AV_PICTURE_TYPE_B:
        log->Debug("(B) Frame .");
        break;
    case AV_PICTURE_TYPE_S:
        log->Debug("Frame : S(GMC)-VOP MPEG4");
        break;
    case AV_PICTURE_TYPE_SI:
        log->Debug("Switching Intra");
        break;
    case AV_PICTURE_TYPE_SP:
        log->Debug("Switching Predicted");
        break;
    case AV_PICTURE_TYPE_BI:
        log->Debug("FF_BI_TYPE");
        break;
    }
}
