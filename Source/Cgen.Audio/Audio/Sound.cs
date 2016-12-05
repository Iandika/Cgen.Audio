﻿using System;
using System.Collections.Generic;
using System.Text;

using OpenTK.Audio;
using OpenTK.Audio.OpenAL;

using Cgen;
using Cgen.Internal.OpenAL;

namespace Cgen.Audio
{
    /// <summary>
    /// Represents a sound that can be played in the audio environment.
    /// </summary>
    public class Sound : SoundSource, IDisposable
    {
        private SoundBuffer _buffer;

        /// <summary>
        /// Gets or sets the <see cref="SoundBuffer"/> containing the audio data to play.
        /// </summary>
        public SoundBuffer Buffer
        {
            get
            {
                return _buffer;
            }
            set
            {
                // First detach from the previous buffer
                if (_buffer != null)
                {
                    Stop();
                    _buffer.DetachSound(this);
                }

                // Assign and use the new buffer
                _buffer = value;
            }
        }

        /// <summary>
        /// Gets or sets a value indicating whether the current <see cref="Sound"/> object is in loop mode.
        /// </summary>
        public override bool IsLooping
        {
            get
            {
                bool looping = false;
                ALChecker.Check(() => AL.GetSource(Handle, ALSourceb.Looping, out looping));

                return looping;
            }
            set
            {
                ALChecker.Check(() => AL.Source(Handle, ALSourceb.Looping, value));
            }
        }

        /// <summary>
        /// Gets or sets the current playing position of the current <see cref="Sound"/> object.
        /// </summary>
        public override TimeSpan PlayingOffset
        {
            get
            {
                float seconds = 0f;
                ALChecker.Check(() => AL.GetSource(Handle, ALSourcef.SecOffset, out seconds));

                return TimeSpan.FromSeconds(seconds);
            }
            set
            {
                ALChecker.Check(() => AL.Source(Handle, ALSourcef.SecOffset, (float)value.TotalSeconds));
            }
        }

        /// <summary>
        /// Gets total duration of current <see cref="Sound"/> object.
        /// </summary>
        public override TimeSpan Duration
        {
            get
            {
                if (_buffer != null)
                {
                    return _buffer.Duration;
                }

                return TimeSpan.Zero;
            }
        }

        /// <summary>
        /// Initializes a new instance of <see cref="Sound"/> class.
        /// </summary>
        public Sound()
        {
        }

        /// <summary>
        /// Initializes a new instance of <see cref="Sound"/> class
        /// with specified <see cref="SoundBuffer"/>.
        /// </summary>
        /// <param name="buffer">The <see cref="SoundBuffer"/> containing souhd sample.</param>
        public Sound(SoundBuffer buffer)
        {
            Buffer = buffer;
        }

        /// <summary>
        /// Start or resume playing the current <see cref="Sound"/> object.
        /// </summary>
        protected override void Play()
        {
            if (Handle <= 0)
            {
                Logger.Warning("SoundSource Handle is not created.\n" +
                    "Use SoundSystem.Play() to play a SoundSource.");

                return;
            }

            // Attach the sound if it's not attached yet
            if (!Buffer.IsAttached(this))
            {
                Buffer.AttachSound(this);
                ALChecker.Check(() => AL.Source(Handle, ALSourcei.Buffer, _buffer.Handle));
            }

            ALChecker.Check(() => AL.SourcePlay(Handle));
        }

        /// <summary>
        /// Pause the current <see cref="Sound"/> object.
        /// </summary>
        protected override void Pause()
        {
            if (Handle <= 0)
            {
                Logger.Warning("SoundSource Handle is not created.\n" +
                    "Use SoundSystem.Pause() to play a SoundSource.");

                return;
            }

            ALChecker.Check(() => AL.SourcePause(Handle));
        }

        /// <summary>
        /// Stop playing the current <see cref="Sound"/> object.
        /// </summary>
        protected override void Stop()
        {
            if (Handle <= 0)
            {
                Logger.Warning("SoundSource Handle is not created.\n" +
                    "Use SoundSystem.Pause() to play a SoundSource.");

                return;
            }

            ALChecker.Check(() => AL.SourceStop(Handle));
        }

        /// <summary>
        /// Releases all resources used by the <see cref="Sound"/>.
        /// </summary>
        public override void Dispose()
        {
            Stop();
            _buffer?.DetachSound(this);

            base.Dispose();
        }

        internal void ResetBuffer()
        {
            // First stop the sound in case it is playing
            Stop();

            // Detach the buffer
            if (_buffer != null)
            {
                ALChecker.Check(() => AL.Source(Handle, ALSourcei.Buffer, 0));
                _buffer.DetachSound(this);
                _buffer = null;
            }
        }
    }
}
