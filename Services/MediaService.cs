using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Windows.Foundation;
using Windows.Media.Control;
using Windows.Storage.Streams;

namespace WinIsland.Services
{
    public sealed class MediaSnapshot
    {
        public bool HasSession;
        public string AppId = "", AppName = "", Title = "", Artist = "";
        public bool IsPlaying, CanPrev, CanNext, CanSeek;
        public TimeSpan Position, Duration;
        public DateTimeOffset UpdatedAt;
        public byte[] Thumbnail;

        public bool SameAs(MediaSnapshot o) =>
            o != null && HasSession == o.HasSession && AppId == o.AppId && Title == o.Title && Artist == o.Artist &&
            IsPlaying == o.IsPlaying && CanPrev == o.CanPrev && CanNext == o.CanNext && CanSeek == o.CanSeek &&
            Position == o.Position && Duration == o.Duration && UpdatedAt == o.UpdatedAt &&
            ReferenceEquals(Thumbnail, o.Thumbnail);
    }

    /// <summary>
    /// Системный медиа-API Windows (GSMTC): видит YouTube в любом браузере, Telegram Desktop,
    /// Spotify, Яндекс Музыку и всё, что показывается в системной плашке громкости.
    /// </summary>
    public sealed class MediaService : IDisposable
    {
        GlobalSystemMediaTransportControlsSessionManager _mgr;
        GlobalSystemMediaTransportControlsSession _session;
        readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);
        Timer _poll;
        int _pending;
        volatile bool _wantThumb;
        string _key;
        byte[] _thumb;
        int _thumbTries;
        MediaSnapshot _last;

        /// <summary>Вызывается в фоновом потоке, только когда что-то реально поменялось.</summary>
        public event Action<MediaSnapshot> Updated;

        public async Task StartAsync()
        {
            try
            {
                _mgr = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
                _mgr.CurrentSessionChanged += (_, _) => Refresh(true);
                _mgr.SessionsChanged += (_, _) => Refresh(true);
            }
            catch (Exception ex) { _mgr = null; App.Log(ex); }

            Refresh(true);
            // страховка: некоторые приложения забывают присылать события
            _poll = new Timer(_ => Refresh(false), null, 2000, 2000);
        }

        public void Refresh(bool thumb)
        {
            if (thumb) _wantThumb = true;
            if (Interlocked.Exchange(ref _pending, 1) == 1) return;
            Task.Run(RunAsync);
        }

        async Task RunAsync()
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                await Task.Delay(35).ConfigureAwait(false); // склеиваем пачку событий в одно обновление
                Volatile.Write(ref _pending, 0);
                bool thumb = _wantThumb;
                _wantThumb = false;

                var snap = await BuildAsync(thumb).ConfigureAwait(false);
                if (!snap.SameAs(_last))
                {
                    _last = snap;
                    Updated?.Invoke(snap);
                }
            }
            catch (Exception ex) { App.Log(ex); }
            finally { _gate.Release(); }
        }

        async Task<MediaSnapshot> BuildAsync(bool thumb)
        {
            var snap = new MediaSnapshot();
            var s = Pick();
            Attach(s);
            if (s == null) { _key = null; _thumb = null; return snap; }

            snap.HasSession = true;
            try { snap.AppId = s.SourceAppUserModelId ?? ""; } catch { }
            snap.AppName = AppNames.Friendly(snap.AppId);

            try
            {
                var pb = s.GetPlaybackInfo();
                if (pb != null)
                {
                    snap.IsPlaying = pb.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
                    var c = pb.Controls;
                    if (c != null)
                    {
                        snap.CanPrev = c.IsPreviousEnabled;
                        snap.CanNext = c.IsNextEnabled;
                        snap.CanSeek = c.IsPlaybackPositionEnabled;
                    }
                }
            }
            catch { }

            try
            {
                var tl = s.GetTimelineProperties();
                if (tl != null)
                {
                    snap.Duration = tl.EndTime - tl.StartTime;
                    snap.Position = tl.Position - tl.StartTime;
                    snap.UpdatedAt = tl.LastUpdatedTime;
                    if (snap.Duration < TimeSpan.Zero) snap.Duration = TimeSpan.Zero;
                    if (snap.Position < TimeSpan.Zero) snap.Position = TimeSpan.Zero;
                }
            }
            catch { }

            GlobalSystemMediaTransportControlsSessionMediaProperties props = null;
            try { props = await s.TryGetMediaPropertiesAsync(); } catch { }
            if (props != null)
            {
                snap.Title = props.Title ?? "";
                snap.Artist = !string.IsNullOrWhiteSpace(props.Artist) ? props.Artist : (props.AlbumArtist ?? "");
            }

            string key = snap.AppId + "\n" + snap.Title + "\n" + snap.Artist;
            if (key != _key) { _key = key; _thumb = null; _thumbTries = 0; thumb = true; }

            IRandomAccessStreamReference thumbRef = null;
            try { thumbRef = props?.Thumbnail; } catch { }
            if (thumbRef != null && (thumb || (_thumb == null && _thumbTries < 3)))
            {
                _thumbTries++;
                var data = await ReadAsync(thumbRef).ConfigureAwait(false);
                if (data != null && (_thumb == null || !data.AsSpan().SequenceEqual(_thumb)))
                    _thumb = data;
            }
            snap.Thumbnail = _thumb;
            return snap;
        }

        static async Task<byte[]> ReadAsync(IRandomAccessStreamReference r)
        {
            try
            {
                using var stream = await r.OpenReadAsync();
                if (stream.Size == 0 || stream.Size > 10_000_000) return null;
                using var reader = new DataReader(stream);
                uint n = await reader.LoadAsync((uint)stream.Size);
                var buf = new byte[n];
                reader.ReadBytes(buf);
                return buf;
            }
            catch { return null; }
        }

        GlobalSystemMediaTransportControlsSession Pick()
        {
            if (_mgr == null) return null;
            GlobalSystemMediaTransportControlsSession cur = null;
            try { cur = _mgr.GetCurrentSession(); } catch { }
            if (IsPlaying(cur)) return cur;
            try
            {
                var list = _mgr.GetSessions();
                foreach (var x in list) if (IsPlaying(x)) return x;   // то, что реально играет — важнее
                if (cur != null) return cur;
                if (_session != null)
                {
                    string id = _session.SourceAppUserModelId;
                    foreach (var x in list) if (x.SourceAppUserModelId == id) return x;
                }
                return list.Count > 0 ? list[0] : null;
            }
            catch { return cur; }
        }

        static bool IsPlaying(GlobalSystemMediaTransportControlsSession s)
        {
            try { return s?.GetPlaybackInfo()?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing; }
            catch { return false; }
        }

        void Attach(GlobalSystemMediaTransportControlsSession s)
        {
            if (ReferenceEquals(s, _session)) return;
            if (_session != null)
            {
                try
                {
                    _session.MediaPropertiesChanged -= OnProps;
                    _session.PlaybackInfoChanged -= OnPlayback;
                    _session.TimelinePropertiesChanged -= OnTimeline;
                }
                catch { }
            }
            _session = s;
            if (s != null)
            {
                try
                {
                    s.MediaPropertiesChanged += OnProps;
                    s.PlaybackInfoChanged += OnPlayback;
                    s.TimelinePropertiesChanged += OnTimeline;
                }
                catch { }
            }
        }

        void OnProps(GlobalSystemMediaTransportControlsSession s, MediaPropertiesChangedEventArgs e) => Refresh(true);
        void OnPlayback(GlobalSystemMediaTransportControlsSession s, PlaybackInfoChangedEventArgs e) => Refresh(false);
        void OnTimeline(GlobalSystemMediaTransportControlsSession s, TimelinePropertiesChangedEventArgs e) => Refresh(false);

        // ---------- управление ----------
        public void PlayPause() => Run(s => s.TryTogglePlayPauseAsync());
        public void Next() => Run(s => s.TrySkipNextAsync());
        public void Prev() => Run(s => s.TrySkipPreviousAsync());
        public void Seek(TimeSpan pos) => Run(s => s.TryChangePlaybackPositionAsync(pos.Ticks));

        void Run(Func<GlobalSystemMediaTransportControlsSession, IAsyncOperation<bool>> f)
        {
            var s = _session;
            if (s == null) return;
            Task.Run(async () =>
            {
                try { await f(s); } catch { }
                Refresh(false);
            });
        }

        public void Dispose()
        {
            _poll?.Dispose();
            Attach(null);
        }
    }

    internal static class AppNames
    {
        static readonly (string key, string name)[] Map =
        {
            ("telegram", "Telegram"),
            ("spotify", "Spotify"),
            ("yandex.music", "Яндекс Музыка"),
            ("yandexmusic", "Яндекс Музыка"),
            ("youtube", "YouTube"),
            ("msedge", "Microsoft Edge"),
            ("chrome", "Google Chrome"),
            ("firefox", "Firefox"),
            ("opera", "Opera"),
            ("yandex", "Яндекс Браузер"),
            ("brave", "Brave"),
            ("vivaldi", "Vivaldi"),
            ("zunemusic", "Медиаплеер"),
            ("microsoft.media", "Медиаплеер"),
            ("applemusic", "Apple Music"),
            ("itunes", "iTunes"),
            ("soundcloud", "SoundCloud"),
            ("discord", "Discord"),
            ("vlc", "VLC"),
            ("aimp", "AIMP"),
            ("foobar", "foobar2000"),
        };

        public static string Friendly(string id)
        {
            if (string.IsNullOrWhiteSpace(id)) return "Медиа";
            string s = id.ToLowerInvariant();
            foreach (var (key, name) in Map) if (s.Contains(key)) return name;

            string n = id;
            int bang = n.LastIndexOf('!');
            if (bang >= 0) n = n.Substring(0, bang);
            n = Path.GetFileNameWithoutExtension(n);
            int us = n.IndexOf('_');
            if (us > 0) n = n.Substring(0, us);
            int dot = n.LastIndexOf('.');
            if (dot >= 0 && dot < n.Length - 1) n = n.Substring(dot + 1);
            return string.IsNullOrWhiteSpace(n) ? "Медиа" : n;
        }
    }
}
