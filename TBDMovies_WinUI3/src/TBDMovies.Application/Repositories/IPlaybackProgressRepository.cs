using System;
using System.Collections.Generic;
using TBDMovies.Domain.Playback;

namespace TBDMovies.Application.Repositories;

public interface IPlaybackProgressRepository
{
    PlaybackProgress? GetByMediaFileId(int mediaFileId);
    PlaybackProgress? GetForMovie(int movieId);
    IReadOnlyDictionary<int, PlaybackProgress> GetForMovies(IEnumerable<int> movieIds);
    PlaybackProgress? GetForEpisode(int episodeId);
    IReadOnlyDictionary<int, PlaybackProgress> GetForEpisodes(IEnumerable<int> episodeIds);
    void SaveProgress(int mediaFileId, double positionSeconds, double? totalDurationSeconds, DateTimeOffset lastWatchedAt);
    void DeleteProgress(int mediaFileId);
    void DeleteProgressForMovie(int movieId);
    void DeleteProgressForEpisode(int episodeId);
    void DeleteProgressForShow(int showId);
}
