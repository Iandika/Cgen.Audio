﻿using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;

using OpenTK.Audio;
using OpenTK.Audio.OpenAL;

using Cgen;
using Cgen.Internal.OpenAL;

namespace Cgen.Audio
{
    /// <summary>
    /// Represents a streamed audio sources.
    /// </summary>
    public abstract class SoundStream : SoundSource
    {
        private const int BUFFER_COUNT   = 3;
        private const int BUFFER_RETRIES = 2;
        private readonly object _mutex   = new object();

        private Thread   _thread;
        private SoundStatus   _state        = SoundStatus.Stopped;
        private bool     _isStreaming  = false;
        private int[]    _buffers      = new int[BUFFER_COUNT];
        private int      _channelCount = 0;
        private int      _sampleRate   = 0;
        private ALFormat _format       = 0;
        private bool     _loop         = false;
        private long     _processed    = 0;
        private bool[]   _endBuffers   = new bool[BUFFER_COUNT];

        /// <summary>
        /// Gets the number of channels used by current <see cref="SoundStream"/> object.
        /// </summary>
        public int ChannelCount
        {
            get { return _channelCount; }
        }

        /// <summary>
        /// Gets the sample rate of current <see cref="SoundStream"/> object.
        /// </summary>
        public int SampleRate
        {
            get { return _sampleRate; }
        }

        /// <summary>
        /// Gets the current status of current <see cref="SoundStream"/> object.
        /// </summary>
        public override SoundStatus Status
        {
            get
            {
                SoundStatus status = base.Status;

                // To compensate for the lag between play() and alSourceplay()
                if (status == SoundStatus.Stopped)
                {
                    lock (_mutex)
                    {
                        if (_isStreaming)
                            status = _state;
                    }
                }

                return status;
            }
        }

        /// <summary>
        /// Gets or sets the current playing position of the current <see cref="SoundStream"/> object.
        /// </summary>
        public override TimeSpan PlayingOffset
        {
            get
            {
                if (_sampleRate > 0 && _channelCount > 0)
                {
                    float seconds = 0f;
                    ALChecker.Check(() => AL.GetSource(Handle, ALSourcef.SecOffset, out seconds));

                    if (seconds == 0)
                    {
                        return TimeSpan.Zero;
                    }

                    var offset = TimeSpan.FromSeconds(seconds + (float)_processed / (float)_sampleRate / _channelCount);
                    if (offset > Duration)
                        offset = Duration;

                    return offset;
                }
                else
                {
                    return TimeSpan.Zero;
                }
            }
            set
            {
                // Get old playing status
                SoundStatus oldStatus = Status;

                // Stop the stream
                Stop();

                // Let the derived class update the current position
                OnSeek(value);

                // Restart streaming
                _processed = (long)(value.TotalSeconds * _sampleRate * _channelCount);

                if (oldStatus == SoundStatus.Stopped)
                    return;

                _state       = oldStatus;
                _isStreaming = true;
                _thread.Start();
            }
        }

        /// <summary>
        /// Gets total duration of current <see cref="SoundStream"/> object.
        /// </summary>
        public override abstract TimeSpan Duration
        {
            get;
        }

        /// <summary>
        /// Gets or sets a value indicating whether the current <see cref="SoundStream"/> object is in loop mode.
        /// </summary>
        public override bool IsLooping
        {
            get { return _loop; }
            set { _loop = value;}
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="SoundStream"/> class.
        /// </summary>
        public SoundStream()
        {
            _thread = new Thread(new ThreadStart(StreamData));
        }

        private void StreamData()
        {
            bool requestStop = false;

            lock (_mutex)
            {
                // Check if the thread was launched Stopped
                if (_state == SoundStatus.Stopped)
                {
                    _isStreaming = false;
                    return;
                }
            }

            // Create the buffers
            ALChecker.Check(() => _buffers = AL.GenBuffers(BUFFER_COUNT));
            for (int i = 0; i < BUFFER_COUNT; ++i)
                _endBuffers[i] = false;

            // Fill the queue
            requestStop = FillQueue();

            // Play the sound
            ALChecker.Check(() => AL.SourcePlay(Handle));

            lock (_mutex)
            {
                // Check if the thread was launched Paused
                if (_state == SoundStatus.Paused)
                    ALChecker.Check(() => AL.SourcePause(Handle));
            }

            for (;;)
            {
                if (AudioDevice.IsDisposed)
                {
                    lock (_mutex)
                        _isStreaming = false;

                    break;
                }

                lock (_mutex)
                {
                    if (!_isStreaming)
                        break;
                }

                // The stream has been interrupted!
                if (base.Status == SoundStatus.Stopped)
                {
                    if (!requestStop)
                    {
                        // Just continue
                        ALChecker.Check(() => AL.SourcePlay(Handle));
                    }
                    else
                    {
                        // End streaming
                        lock(_mutex)
                            _isStreaming = false;
                    }
                }

                // Get the number of buffers that have been processed (i.e. ready for reuse)
                int nbProcessed = 0;
                ALChecker.Check(() => AL.GetSource(Handle, ALGetSourcei.BuffersProcessed, out nbProcessed));
                

                while (nbProcessed-- > 0)
                {
                    // Pop the first unused buffer from the queue
                    int buffer = 0;
                    ALChecker.Check(() => buffer = AL.SourceUnqueueBuffer(Handle));

                    // Find its number
                    int bufferNum = 0;
                    for (int i = 0; i < BUFFER_COUNT; ++i)
                        if (_buffers[i] == buffer)
                        {
                            bufferNum = i;
                            break;
                        }

                    // Retrieve its size and add it to the samples count
                    if (_endBuffers[bufferNum])
                    {
                        // This was the last buffer: reset the sample count
                        _processed = 0;
                        _endBuffers[bufferNum] = false;
                    }
                    else
                    {
                        int size = 0, bits = 0;
                        ALChecker.Check(() => AL.GetBuffer(buffer, ALGetBufferi.Size, out size));
                        ALChecker.Check(() => AL.GetBuffer(buffer, ALGetBufferi.Bits, out bits));

                        // Bits can be 0 if the format or parameters are corrupt, avoid division by zero
                        if (bits == 0)
                        {
                            Logger.Warning("Bits in sound stream are 0: make sure that the audio format is not corrupt " +
                                   "and initialize() has been called correctly");

                            // Abort streaming (exit main loop)
                            lock (_mutex)
                                _isStreaming = false;
                            requestStop = true;
                            break;
                        }
                        else
                        {
                            _processed += size / (bits / 8);
                        }
                    }

                    // Fill it and push it back into the playing queue
                    if (!requestStop)
                    {
                        if (FillAndPushBuffer(bufferNum))
                            requestStop = true;
                    }
                }

                // Leave some time for the other threads if the stream is still playing
                if (base.Status != SoundStatus.Stopped)
                    Thread.Sleep(10);
            }

            // Stop the playback
            ALChecker.Check(() => AL.SourceStop(Handle));

            // Dequeue any buffer left in the queue
            ClearQueue();

            // Delete the buffers
            ALChecker.Check(() => AL.Source(Handle, ALSourcei.Buffer, 0));
            ALChecker.Check(() => AL.DeleteBuffers(_buffers));
        }

        private bool FillAndPushBuffer(int bufferNum, bool immediateLoop = false)
        {
            bool requestStop = false;

            // Acquire audio data
            short[] samples = null;
            for (int retryCount = 0; !OnGetData(out samples) && (retryCount < BUFFER_RETRIES); ++retryCount)
            {
                // Mark the buffer as the last one (so that we know when to reset the playing position)
                _endBuffers[bufferNum] = true;

                // Check if the stream must loop or stop
                if (!_loop)
                {
                    // Not looping: request stop
                    requestStop = true;
                    break;
                }

                // Return to the beginning of the stream source
                OnSeek(TimeSpan.Zero);

                // If we got data, break and process it, else try to fill the buffer once again
                if (samples != null && (samples.Length > 0))
                {
                    break;
                }

                // If immediateLoop is specified, we have to immediately adjust the sample count
                if (immediateLoop)
                {
                    // We just tried to begin preloading at EOF: reset the sample count
                    _processed = 0;
                    _endBuffers[bufferNum] = false;
                }

                // We're a looping sound that got no data, so we retry onGetData()
            }

            // Fill the buffer if some data was returned
            if (samples != null && samples.Length > 0)
            {
                int buffer = _buffers[bufferNum];

                // Fill the buffer
                int size = samples.Length * sizeof(short);
                ALChecker.Check(() => AL.BufferData(buffer, _format, samples, size, _sampleRate));

                // Push it into the sound queue
                ALChecker.Check(() => AL.SourceQueueBuffer(Handle, buffer));
            }
            else
            {
                // If we get here, we most likely ran out of retries
                requestStop = true;
            }

            return requestStop;
        }

        private bool FillQueue()
        {
            // Fill and enqueue all the available buffers
            bool requestStop = false;
            for (int i = 0; (i < BUFFER_COUNT) && !requestStop; ++i)
            {
                if (FillAndPushBuffer(i, (i == 0)))
                    requestStop = true;
            }

            return requestStop;
        }

        private void ClearQueue()
        {
            // Get the number of buffers still in the queue
            int nbQueued = 0;
            ALChecker.Check(() => AL.GetSource(Handle, ALGetSourcei.BuffersQueued, out nbQueued));

            // Dequeue them all
            int buffer = 0;
            for (int i = 0; i < nbQueued; ++i)
                ALChecker.Check(() => buffer = AL.SourceUnqueueBuffer(Handle));

            _processed = 0;
        }

        /// <summary>
        /// Request a new chunk of audio samples from the stream source.
        /// </summary>
        /// <param name="samples">The audio chunk that contains audio samples.</param>
        /// <returns><code>true</code> if reach the end of stream, otherwise false.</returns>
        protected abstract bool OnGetData(out short[] samples);

        /// <summary>
        /// Change the current playing position in the stream source.
        /// </summary>
        /// <param name="time">Seek to specified time.</param>
        protected abstract void OnSeek(TimeSpan time);

        /// <summary>
        /// Performs initialization steps by providing the audio stream parameters.
        /// </summary>
        /// <param name="channelCount">The number of channels of current <see cref="SoundStream"/> object.</param>
        /// <param name="sampleRate">The sample rate, in samples per second.</param>
        protected virtual void Initialize(int channelCount, int sampleRate)
        {
            // Reset the current states
            _channelCount = channelCount;
            _sampleRate   = sampleRate;
            _processed    = 0;
            _isStreaming  = false;

            // Deduce the format from the number of channels
            _format = AudioDevice.GetFormat(channelCount);

            // Check if the format is valid
            if (_format == 0)
            {
                throw new NotSupportedException("The specified number of channels (" + _channelCount.ToString() + ") is not supported.");
            }
        }

        /// <summary>
        /// Start or resume playing current <see cref="SoundStream"/> object.
        /// </summary>
        protected override void Play()
        {
            // Check if the sound parameters have been set
            if (_format == 0)
            {
                throw new InvalidOperationException(
                    "Audio parameters must be initialized before played.");
            }

            bool isStreaming = false;
            SoundStatus state = SoundStatus.Stopped;

            lock (_mutex)
            {
                isStreaming = _isStreaming;
                state       = _state;
            }


            if (isStreaming && (state == SoundStatus.Paused))
            {
                // If the sound is paused, resume it
                lock (_mutex)
                {
                    _state = SoundStatus.Playing;
                    ALChecker.Check(() => AL.SourcePlay(Handle));
                }

                return;
            }
            else if (isStreaming && (state == SoundStatus.Playing))
            {
                // If the sound is playing, stop it and continue as if it was stopped
                Stop();
            }

            // Move to the beginning
            OnSeek(TimeSpan.Zero);

            // Start updating the stream in a separate thread to avoid blocking the application
            _processed   = 0;
            _isStreaming = true;
            _state       = SoundStatus.Playing;

            _thread.Start();
        }

        /// <summary>
        /// Pause the current <see cref="SoundStream"/> object.
        /// </summary>
        protected override void Pause()
        {
            lock (_mutex)
            {
                if (!_isStreaming)
                    return;

                _state = SoundStatus.Paused;
            }

            ALChecker.Check(() => AL.SourcePause(Handle));
        }

        /// <summary>
        /// Stop playing the current <see cref="SoundStream"/> object.
        /// </summary>
        protected override void Stop()
        {
            lock (_mutex)
            {
                _isStreaming = false;
            }

            // Wait for the thread to terminate
            _thread.Join();

            // Move to the beginning
            OnSeek(TimeSpan.Zero);

            // Reset the playing position
            _processed = 0;
        }

        /// <summary>
        /// Releases all resources used by <see cref="SoundStream"/>.
        /// </summary>
        public override void Dispose()
        {
            lock (_mutex)
            {
                _isStreaming = false;
            }

            _thread.Join();
            base.Dispose();
        }
    }
}
