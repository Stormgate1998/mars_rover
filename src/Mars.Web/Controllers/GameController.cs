using OpenTelemetry.Metrics;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using Prometheus;
using HistogramConfiguration = Prometheus.HistogramConfiguration;

namespace Mars.Web.Controllers;

[ApiController]
[Route("[controller]")]
public class GameController : ControllerBase
{
    ConcurrentDictionary<string, GameManager> games;
    private readonly ConcurrentDictionary<string, string> tokenMap;
    private readonly ILogger<GameController> logger;
    private readonly ExtraApiClient httpClient;
    private readonly MarsCounters counters;

    //Histograms and counters for prometheus data
    private static readonly Counter joinCalls = Metrics.CreateCounter(
    "my_function_calls_total",
    "Total number of calls to my function");

    private static readonly Histogram joinfunctionDuration = Metrics.CreateHistogram(
        "my_function_duration_seconds",
        "Duration of my function in seconds",
        new HistogramConfiguration
        {
            Buckets = Histogram.LinearBuckets(start: 0.1, width: 0.1, count: 10), // Buckets from 0.1s to 1s
            LabelNames = new[] { "status" } // Add a label to differentiate between successful and failed function calls
        });



    //End prometheus input

    public GameController(MultiGameHoster multiGameHoster, ILogger<GameController> logger, ExtraApiClient httpClient, MarsCounters counters)
    {
        this.games = multiGameHoster.Games;
        this.tokenMap = multiGameHoster.TokenMap;
        this.logger = logger;
        this.httpClient = httpClient;
        this.counters = counters;
    }

    /// <summary>
    /// Join an existing game.  You can join in the 'Joining' state, or in the 'Playing' state.
    /// </summary>
    /// <param name="gameId">What game you'd like to join</param>
    /// <param name="name">What your player name should be</param>
    /// <returns></returns>
    [HttpGet("[action]")]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(JoinResponse))]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<JoinResponse>> Join(string gameId, string name)
    {
        
        if (games.TryGetValue(gameId, out GameManager? gameManager))
        {
            joinCalls.Inc();
            var stopwatch = Stopwatch.StartNew();
            try
            {
                using var activity = ActivitySources.MarsWeb.StartActivity("Join Game", kind: ActivityKind.Consumer);
                activity?.AddTag("gameid", gameId);
                activity?.AddTag("name", name);
                activity?.AddEvent(new ActivityEvent("joined game event"));

                var joinResult = gameManager.Game.Join(name);
                using (logger.BeginScope("ScopeUserToken: {ScopeUser} GameId: {ScopeGameId} ", joinResult.Token.Value, gameId))
                {
                    tokenMap.TryAdd(joinResult.Token.Value, gameId);
                    logger.LogWarning("Player {name} joined game {gameId}", name, gameId);

                    var weather = await httpClient.GetWeatherAsync();

                    counters.GameJoins.Add(1);
                }
                return new JoinResponse
                {
                    Token = joinResult.Token.Value,
                    StartingY = joinResult.PlayerLocation.Y,
                    StartingX = joinResult.PlayerLocation.X,
                    Neighbors = joinResult.Neighbors.ToDto(),
                    LowResolutionMap = joinResult.LowResolutionMap.ToDto(),
                    TargetX = joinResult.TargetLocation.X,
                    TargetY = joinResult.TargetLocation.Y,
                    Orientation = joinResult.Orientation.ToString()
                };
            }
            catch (TooManyPlayersException)
            {
                logger.LogError("Player {name} failed to join game {gameId}. Too many players", name, gameId);
                return Problem("Cannot join game, too many players.", statusCode: 400, title: "Too many players");
            }
            finally
            {
                stopwatch.Stop();
                joinfunctionDuration.Observe(stopwatch.Elapsed.TotalSeconds);
            }
        }
        else
        {
            logger.LogError("Player {name} failed to join game {gameId}. Game id not found", name, gameId);
            return Problem("Unrecognized game id.", statusCode: 400, title: "Bad Game ID");
        }
    }

    [HttpGet("[action]")]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(StatusResponse))]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public ActionResult<StatusResponse> Status(string token)
    {
        var tokenHasGame = tokenMap.TryGetValue(token, out string? gameId);
        using (logger.BeginScope("ScopeUserToken: {ScopeUser} GameId: {ScopeGameId} ", token, gameId))
        {
            if (tokenHasGame)
            {
                if (games.TryGetValue(gameId, out var gameManager))
                {
                    if (gameManager.Game.TryTranslateToken(token, out _))
                    {
                        return new StatusResponse { Status = gameManager.Game.GameState.ToString() };
                    }
                }
            }
            logger.LogError("Unrecogized token {token}", token);
            return Problem("Unrecognized token", statusCode: 400, title: "Bad Token");
        }
    }

    /// <summary>
    /// Move the Perseverance rover.
    /// </summary>
    /// <param name="token"></param>
    /// <param name="direction">If left out, a default direction of Forward will be assumed.</param>
    /// <returns></returns>
    [HttpGet("[action]")]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(PerseveranceMoveResponse))]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public ActionResult<PerseveranceMoveResponse> MovePerseverance(string token, Direction direction)
    {
        var tokenHasGame = tokenMap.TryGetValue(token, out string? gameId);

        using (logger.BeginScope("ScopeUserToken: {ScopeUser} GameId: {ScopeGameId} ", token, gameId))
        {
            if (tokenHasGame)
            {
                if (games.TryGetValue(gameId, out var gameManager))
                {
                    PlayerToken? playerToken;
                    if (!gameManager.Game.TryTranslateToken(token, out playerToken))
                    {
                        logger.LogError("Unrecogized token {token}", token);
                        return Problem("Unrecognized token", statusCode: 400, title: "Bad Token");
                    }

                    if (gameManager.Game.GameState != GameState.Playing)
                    {
                        logger.LogError($"Could not move: Game not in Playing state.");
                        return Problem("Unable to move", statusCode: 400, title: "Game not in Playing state.");
                    }

                    try
                    {
                        var moveResult = gameManager.Game.MovePerseverance(playerToken!, direction);
                        return new PerseveranceMoveResponse
                        {
                            X = moveResult.Location.X,
                            Y = moveResult.Location.Y,
                            BatteryLevel = moveResult.BatteryLevel,
                            Neighbors = moveResult.Neighbors.ToDto(),
                            Message = moveResult.Message,
                            Orientation = moveResult.Orientation.ToString()
                        };
                    }
                    catch (Exception ex)
                    {
                        logger.LogError("Could not move: {message}", ex.Message);
                        return Problem("Unable to move", statusCode: 400, title: ex.Message);
                    }
                }

            }
            logger.LogError("Unrecogized token {token}", token);
            return Problem("Unrecognized token", statusCode: 400, title: "Bad Token");
        }
    }

    /// <summary>
    /// Move the Ingenuity helicopter.
    /// </summary>
    /// <param name="token"></param>
    /// <param name="destinationColumn"></param>
    /// <param name="destinationRow"></param>
    /// <returns></returns>
    [HttpGet("[action]")]
    [ProducesResponseType(StatusCodes.Status200OK, Type = typeof(IngenuityMoveResponse))]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public ActionResult<IngenuityMoveResponse> MoveIngenuity(string token, int destinationRow, int destinationColumn)
    {
        var tokenHasGame = tokenMap.TryGetValue(token, out string? gameId);
        using (logger.BeginScope("ScopeUserToken: {ScopeUser} GameId: {ScopeGameId} ", token, gameId))
        {
            if (tokenHasGame)
            {
                if (games.TryGetValue(gameId, out var gameManager))
                {
                    PlayerToken? playerToken;
                    if (!gameManager.Game.TryTranslateToken(token, out playerToken))
                    {
                        logger.LogError("Unrecogized token {token}", token);
                        return Problem("Unrecognized token", statusCode: 400, title: "Bad Token");
                    }

                    if (gameManager.Game.GameState != GameState.Playing)
                    {
                        logger.LogError("Could not move: Game not in Playing state.");
                        return Problem("Unable to move", statusCode: 400, title: "Game not in Playing state.");
                    }

                    try
                    {
                        var moveResult = gameManager.Game.MoveIngenuity(playerToken!, new Location(destinationRow, destinationColumn));
                        return new IngenuityMoveResponse
                        {
                            X = moveResult.Location.X,
                            Y = moveResult.Location.Y,
                            Neighbors = moveResult.Neighbors.ToDto(),
                            Message = moveResult.Message,
                            BatteryLevel = moveResult.BatteryLevel
                        };
                    }
                    catch (Exception ex)
                    {
                        logger.LogError("Could not move: {exceptionMessage}", ex.Message);
                        return Problem("Unable to move", statusCode: 400, title: ex.Message);
                    }
                }
            }
            logger.LogError("Unrecogized token {token}", token);
            return Problem("Unrecognized token", statusCode: 400, title: "Bad Token");
        }
    }
}
