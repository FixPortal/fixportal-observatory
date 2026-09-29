namespace AiObservatory.Ingest.Services.GitHub;

// Thrown when X-RateLimit-Remaining drops below the safety threshold mid-poll.
// The ingestion service catches this to skip remaining repositories in the current
// poll without failing the worker.
public class GitHubRateLimitExceededException(int remaining)
    : Exception(
        $"GitHub API rate limit nearly exhausted ({remaining} requests remaining); aborting remaining repos this cycle."
    );
