using System;
using TBDMovies.Application.Repositories;

namespace TBDMovies.Application.Services;

public sealed class LibraryRevisionTracker : ILibraryRevisionTracker
{
    private readonly object _gate = new();
    private long _catalogRevision;
    private long _personalRevision;
    private long _playbackRevision;

    public long CatalogRevision
    {
        get
        {
            lock (_gate) return _catalogRevision;
        }
    }

    public long PersonalRevision
    {
        get
        {
            lock (_gate) return _personalRevision;
        }
    }

    public long PlaybackRevision
    {
        get
        {
            lock (_gate) return _playbackRevision;
        }
    }

    public LibrarySnapshotToken Snapshot
    {
        get
        {
            lock (_gate)
            {
                return new LibrarySnapshotToken(_catalogRevision, _personalRevision, _playbackRevision);
            }
        }
    }

    public long IncrementCatalog()
    {
        lock (_gate)
        {
            return ++_catalogRevision;
        }
    }

    public long IncrementPersonal()
    {
        lock (_gate)
        {
            return ++_personalRevision;
        }
    }

    public long IncrementPlayback()
    {
        lock (_gate)
        {
            return ++_playbackRevision;
        }
    }

    public void IncrementAll()
    {
        lock (_gate)
        {
            _catalogRevision++;
            _personalRevision++;
            _playbackRevision++;
        }
    }
}
