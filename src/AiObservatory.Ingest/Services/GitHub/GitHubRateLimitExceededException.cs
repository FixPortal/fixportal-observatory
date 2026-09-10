namespace AiObservatory.Ingest.Services.GitHub;

// Thrown when X-RateLimit-Remaining drops below the safety threshold mid-poll.
// The ingestion service (GitHubIngestionService) catches this to abort the rest
// of THIS poll cycle's repos without failing the whole worker — see Task 8.
public class GitHubRateLimitExceededException(int remaining)
    : Exception(
        $"GitHub API rate limit nearly exhausted ({remaining} requests remaining); aborting remaining repos this cycle."
    );
