// LCS Discord Bot
// V1.2 
// Evan Greavu
// Bot.cs

using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Discord.WebSocket;

namespace LCSDiscordBot
{
    class Program
    {
        const string SCORES_FILE = "scores.csv";
        const string BETS_FILE = "bets.csv";
        public async static Task Main(string[] args)
        {
            Console.WriteLine($"{DateTime.Now:MM-dd-yyyy HH:mm}: Loading Scorekeeper from file '{SCORES_FILE}' and '{BETS_FILE}'");
            LCSDiscordBot scorekeeper = new LCSDiscordBot(SCORES_FILE, BETS_FILE);

            Console.WriteLine($"{DateTime.Now:MM-dd-yyyy HH:mm}: Starting Scorekeeper loop.");

            await scorekeeper.Start();
            Console.WriteLine($"{DateTime.Now:MM-dd-yyyy HH:mm}: Scorekeeper loop exited.");
        }
    }

    class LCSDiscordBot
    {
		const bool _DEBUG = false;
        static readonly Regex REGEX_MESSAGEPARSE = new Regex(@"!([\w\d\s.]+)");
        const int LOOP_DELAY_SECONDS = 60;
        const int BETBUFFER_MINS = 12;
        const ulong ID_MAINCHANNEL = 800000000000000000; // redacted
        const ulong ID_TESTCHANNEL = 900000000000000000;
        const string DISCORD_TOKEN = ""; // redacted

        DiscordSocketClient Client;
        SocketTextChannel MainChannel;
        Scoreboard Scoreboard;
        MatchRecord MatchRecord;
        Schedule Schedule;
        BetBook BetBook;
        string BetBookPath = "bets.csv";
        bool started = false;

        public LCSDiscordBot()
        {
            Scoreboard = new Scoreboard();
            MatchRecord = MatchRecord.GetOnlineMatchRecord();
            Schedule = Schedule.GetOnlineSchedule();
            BetBook = new BetBook();
        }

        public LCSDiscordBot(string scoreboardPath, string betBookPath = "")
        {
            if (File.Exists(scoreboardPath))
            {
                Scoreboard = Scoreboard.FromFile(scoreboardPath);
            }
            else
            {
                Scoreboard = new Scoreboard();
                Scoreboard.SaveToFile();
            }
            if (File.Exists(betBookPath))
            {
                BetBook = BetBook.FromFile(betBookPath);
                BetBookPath = betBookPath;
            }
            else
            {
                BetBook = new BetBook();
                BetBook.SaveToFile(BetBookPath);
            }
            MatchRecord = MatchRecord.GetOnlineMatchRecord();
            Schedule = Schedule.GetOnlineSchedule();
        }

        public LCSDiscordBot(Scoreboard scores) : this() // For Debug
        {
            Scoreboard = scores;
        }

        public async Task ProcessMessage(SocketMessage message)
        {
            var tryMatch = REGEX_MESSAGEPARSE.Match(message.Content);
            if (tryMatch.Success)
            {
                var words = tryMatch.Groups[1].Value.Split(' ');
                string command = words[0];
                IEnumerable<string> args = words.TakeLast(words.Count() - 1);
                string argListPrint = "";
                foreach (string arg in args)
                    argListPrint += arg + " ";
                Console.WriteLine("Attempting to interpret command '{0}' with args: {1}", command, argListPrint);

                // Process possible command
                await ProcessCommand(message.Author.Id, message.Channel as SocketTextChannel, command, args.ToList());
            }
            else
            {
                Console.WriteLine("Invalid command received: {0}", message.Content);
            }
        }

        public async Task ProcessCommand(ulong discordId, SocketTextChannel replyChannel, string command, IList<string> args)
        {
            if (!Scoreboard.ContainsKey(discordId)) // Register player who made command if not yet registered
            {
                string name = ResolvePlayerName(discordId);
                Scoreboard[discordId] = new PlayerScore(discordId, name);
                Scoreboard.SaveToFile();
                Console.WriteLine($"New player registered: {name} (DiscordID {discordId})");
            }

            command = command.ToLower().Trim();
            int argCount = args.Count;

            switch (command) // Command switch
            {
                case "score":
                case "money":
                case "myscore":
                    await InterpretCommand_PlayerScore(discordId, replyChannel);
                    break;
                case "scores":
                case "allscores":
                case "scoreboard":
                    await InterpretCommand_AllScores(replyChannel);
                    break;
                case "games":
                case "upcoming":
                case "schedule":
                    await InterpretCommand_Schedule(replyChannel);
                    break;
                case "mybets":
                case "bets":
                    await InterpretCommand_PlayerBets(discordId, replyChannel);
                    break;
                case "allbets":
                    await InterpretCommand_AllBets(replyChannel);
                    break;
                case "nextgame":
                case "next":
                    await InterpretCommand_NextGame(replyChannel, args);
                    break;
                case "predict":
                    if (argCount == 1 || argCount == 3)
                        await InterpretCommand_Predict(discordId, replyChannel, args);
                    else
                        await SayInChannel(replyChannel, "Usage: !predict <Team> or !predict <Team1> beat <Team2>");
                    break;
                case "placebet":
                case "makebet":
                case "bet":
                    if (argCount == 2 || argCount == 4)
                        await InterpretCommand_Bet(discordId, replyChannel, args);
                    else
                        await SayInChannel(replyChannel, "Usage: !bet <Wager> <Team> or !bet <Wager> <Team1> beat <Team2>");
                    break;
                default: // No matching command

                    break;
            }
        }

        async Task InterpretCommand_PlayerScore(ulong discordId, SocketTextChannel replyChannel)
        {
            if (Scoreboard.ContainsKey(discordId))
            {
                var myScore = Scoreboard[discordId];
                await SayInChannel(replyChannel, $"Score for {myScore.Name}:\n{myScore.Money:c} after {myScore.BetCount} bets ({myScore.MoneyFromBets:+$0.00;-$0.00;$0}). " +
                    $"Prediction record: {myScore.PredictionsRight} - {myScore.PredictionsWrong} (+${myScore.MoneyFromPredictions})");
            }
            else
            {
                await SayInChannel(replyChannel, $"You are not a registered player yet. Place a Bet or Prediction to start. " +
                    $"You'll start with ${PlayerScore.STARTING_MONEY}.");
            }
        }

        async Task InterpretCommand_AllScores(SocketTextChannel replyChannel)
        {
            if (Scoreboard.Count == 0)
            {
                await SayInChannel(replyChannel, "The scoreboard is empty - there are no registered players yet.");
            }
            else
            {
                string scoreMessage = "Scoreboard:\n";
                var sortedScores = Scoreboard.Values.ToList();
                sortedScores.Sort((item1, item2) => item1.CompareTo(item2));
                int place = 1;
                foreach (var playerScore in sortedScores)
                {
                    scoreMessage += $"{place++}. {playerScore.Name}: {playerScore.Money:c} ({playerScore.BetCount} bets, {playerScore.MoneyFromBets:+$0.00;-$0.00;$0}) " +
                        $"Predictions: {playerScore.PredictionsRight} - {playerScore.PredictionsWrong} (+${playerScore.MoneyFromPredictions})\n";
                }
                await SayInChannel(replyChannel, scoreMessage);
            }
        }

        async Task InterpretCommand_Schedule(SocketTextChannel replyChannel)
        {
            var futureGames = Schedule.Where(match => DateTime.Now < match.Time + TimeSpan.FromMinutes(10));
            if (futureGames.Count() <= 0)
                await SayInChannel(replyChannel, "There are no games left in the season.");
            else
            {
                string scheduleMessage = "Upcoming LCS games:\n";
                foreach (var futureGame in futureGames.Take(futureGames.Count() >= 5 ? 5 : futureGames.Count()))
                {
                    scheduleMessage += futureGame.ToString() + "\n";
                }
                await SayInChannel(replyChannel, scheduleMessage);
            }
        }

        async Task InterpretCommand_PlayerBets(ulong discordId, SocketTextChannel replyChannel)
        {
            var myBets = BetBook.GetBetsForPlayer(discordId);
            var myPredictions = BetBook.GetPredictionsForPlayer(discordId);
            if (myBets.Count + myPredictions.Count == 0)
                await SayInChannel(replyChannel, "You do not have any active bets or predictions.");
            else
            {
                string betsString = "";
                if (myBets.Count > 0)
                {
                    betsString += "Your active bets:\n";
                    myBets.ForEach(bet => betsString += $"{bet}\n");
                }
                if (myPredictions.Count > 0)
                {
                    betsString += "Your predictions:\n";
                    myPredictions.ForEach(pred => betsString += $"{pred}\n");
                }
                await SayInChannel(replyChannel, betsString);
            }
        }

        async Task InterpretCommand_AllBets(SocketTextChannel replyChannel)
        {
            if (BetBook.AllCount == 0)
                await SayInChannel(replyChannel, "There are no active bets or predictions.");
            else
            {
                string betString = "";
                if (BetBook.BetCount > 0)
                {
                    betString += "All active bets:\n";
                    BetBook.GetAllBets().ForEach(bet => betString += $"{ResolvePlayerName(bet.DiscordID)} bet {bet}\n");
                }
                if (BetBook.PredictionCount > 0)
                {
                    betString += "Active predictions:\n";
                    BetBook.GetAllPredictions().ForEach(pred => betString += $"{ResolvePlayerName(pred.DiscordID)} thinks {pred}\n");
                }
                await SayInChannel(replyChannel, betString);
            }
        }

        async Task InterpretCommand_NextGame(SocketTextChannel replyChannel, IList<string> args)
        {
            if (args.Count == 1)
            {
                LCSTeam team_check = LCSTeam.ParseTeam(args[0]);
                if (team_check.ID == LCSTeam.TeamID.Unknown)
                    await SayInChannel(replyChannel, $"Unknown LCS team '{args[0]}'");
                else
                {
                    var nextMatchCheck = Schedule.GetNextMatchForTeam(team_check);
                    if (nextMatchCheck == null)
                        await SayInChannel(replyChannel, $"{team_check} has no matches left in the season.");
                    else
                        await SayInChannel(replyChannel, $"Next match for {team_check}: {nextMatchCheck}");
                }
            }
            else if (args.Count == 2) // Checking for match between two teams
            {
                LCSTeam check1 = LCSTeam.ParseTeam(args[0]);
                if (check1.ID == LCSTeam.TeamID.Unknown)
                {
                    await SayInChannel(replyChannel, $"Unknown LCS team '{args[0]}'");
                    return;
                }
                LCSTeam check2 = LCSTeam.ParseTeam(args[1]);
                if (check2.ID == LCSTeam.TeamID.Unknown)
                {
                    await SayInChannel(replyChannel, $"Unknown LCS team '{args[1]}'");
                    return;
                }
                var nextMatchup = Schedule.GetNextMatchForMatchup(check1, check2);
                if (nextMatchup == null)
                    await SayInChannel(replyChannel, $"{check1} and {check2} are not playing each other for the rest of the season.");
                else
                    await SayInChannel(replyChannel, $"{check1} next plays {check2} at {nextMatchup.Time:h:mm tt M/dd}");

            }
            else
                await SayInChannel(replyChannel, "Usage: !next <Team> or !next <Team1> <Team2>");
        }

        async Task InterpretCommand_Predict(ulong discordId, SocketTextChannel replyChannel, IList<string> args)
        {
            // Parse arguments
            LCSTeam winner = LCSTeam.ParseTeam(args[0]);
            LCSTeam loser;
            ScheduledMatch nextScheduledMatch;

            if (winner.ID == LCSTeam.TeamID.Unknown)
            {
                await SayInChannel(replyChannel, $"Unknown LCS team '{args[0]}'");
                return;
            }

            if (args.Count == 3) // argCount == 1: 'beats' middle arg, loser is team2 arg
            {
                loser = LCSTeam.ParseTeam(args[2]);
                if (loser.ID == LCSTeam.TeamID.Unknown)
                {
                    await SayInChannel(replyChannel, $"Unknown LCS team '{args[2]}'");
                    return;
                }
                else
                {
                    nextScheduledMatch = Schedule.GetNextMatchForMatchup(winner, loser);
                    if (nextScheduledMatch == null)
                    {
                        await SayInChannel(replyChannel, $"{winner} has no upcoming games against {loser}.");
                        return;
                    }
                }
            }
            else // argCount == 1: loser is next scheduled opponent
            {
                nextScheduledMatch = Schedule.GetNextMatchForTeam(winner);
                if (nextScheduledMatch == null)
                {
                    await SayInChannel(replyChannel, $"{winner} has no games left in the season.");
                    return;
                }
                else if (nextScheduledMatch.Team1.Equals(winner))
                    loser = nextScheduledMatch.Team2;
                else
                    loser = nextScheduledMatch.Team1;
            }

            List<Prediction> matchingPreds = BetBook.GetPredictionsForPlayerAndMatch(discordId, nextScheduledMatch);
            if (matchingPreds.Count == 0) // This is a new Prediction
            {
                Prediction pred = BetBook.CreatePrediction(discordId, winner, loser, nextScheduledMatch.Time);
                await SayInChannel(replyChannel, $"Prediction made: {pred}");
            }
            else // Prediction update
            {
                Prediction firstMatchingPred = matchingPreds.First();
                if (firstMatchingPred.Victor.Equals(winner) && firstMatchingPred.Loser.Equals(loser))
                {
                    await SayInChannel(replyChannel, $"You already predicted that {firstMatchingPred}");
                }
                else
                {
                    Prediction pred = BetBook.SwapPredictionAtId(matchingPreds.First().ID, discordId, winner, loser, nextScheduledMatch.Time);
                    await SayInChannel(replyChannel, $"Prediction swapped: {pred}");
                }
            }
        }

        async Task InterpretCommand_Bet(ulong discordId, SocketTextChannel replyChannel, IList<string> args)
        {
            // Parse and verify arguments
            LCSTeam winner = LCSTeam.ParseTeam(args[1]);
            LCSTeam loser;
            ScheduledMatch nextScheduledMatch;
            decimal wager;

            if (winner.ID == LCSTeam.TeamID.Unknown)
            {
                await SayInChannel(replyChannel, $"Unknown LCS team '{args[1]}'");
                return;
            }
            if (!decimal.TryParse(args[0].Trim('$'), out wager))
            {
                await SayInChannel(replyChannel, "Please enter an amount of money. Usage: !bet <Wager> <Team> or !bet <Wager> <Team1> beat <Team2>)");
                return;
            }
            if (wager <= 0)
            {
                await SayInChannel(replyChannel, "Wager must be greater than $0.");
                return;
            }
            if (args.Count == 4) // !bet <Wager> <Team1> beat <Team2>: Bet on next match between teams
            {
                loser = LCSTeam.ParseTeam(args[3]);
                if (loser.ID == LCSTeam.TeamID.Unknown)
                {
                    await SayInChannel(replyChannel, $"Unknown LCS team '{args[3]}'");
                    return;
                }
                nextScheduledMatch = Schedule.GetNextMatchForMatchup(winner, loser);
                if (nextScheduledMatch == null)
                {
                    await SayInChannel(replyChannel, $"{winner} has no upcoming games against {loser}.");
                    return;
                }
            }
            else // !bet <Wager> <Team>: Bet on next match for team
            {
                nextScheduledMatch = Schedule.GetNextMatchForTeam(winner);
                if (nextScheduledMatch == null)
                {
                    await SayInChannel(replyChannel, $"Team {winner} has no games left in the season.");
                    return;
                }
                if (nextScheduledMatch.Team1.Equals(winner))
                    loser = nextScheduledMatch.Team2;
                else
                    loser = nextScheduledMatch.Team1;
            }

            // Make sure match is not occurring
            if (DateTime.Now > nextScheduledMatch.Time + TimeSpan.FromMinutes(BETBUFFER_MINS)) // If next game is within this hour
            {
                await SayInChannel(replyChannel, $"{winner} is currently playing! ({nextScheduledMatch}) " +
                    $"Wait until afer the current match to place a bet.");
                return;
            }

            // Make or update bet
            List<Bet> matchingBets = BetBook.GetBetsForPlayerAndMatch(discordId, nextScheduledMatch);
            if (matchingBets.Count == 0) // No matching bet exists - Create a new bet
            {
                if (Scoreboard[discordId].Money < wager)
                {
                    await SayInChannel(replyChannel, $"You can't bet that much. You have {Scoreboard[discordId].Money:c}.");
                }
                else
                {
                    Bet newBet = BetBook.CreateBet(discordId, winner, loser, nextScheduledMatch.Time, wager);
                    Scoreboard.MakePlayerBet(discordId, wager);
                    await SayInChannel(replyChannel, $"Bet created: {newBet}");
                }
            }
            else // Matching bet exists
            {
                Bet firstMatchingBet = matchingBets.First();
                if (firstMatchingBet.Victor.Equals(winner) && firstMatchingBet.Loser.Equals(loser) && firstMatchingBet.Wager == wager) // Bet already exists
                {
                    await SayInChannel(replyChannel, $"You already have an existing bet like that: {firstMatchingBet}");
                }
                else if (Scoreboard[discordId].Money + firstMatchingBet.Wager < wager) // Cannot afford to update bet
                {
                    await SayInChannel(replyChannel, $"You can't change your bet by that much. You have {Scoreboard[discordId].Money:c}.");
                }
                else // Update bet
                {
                    Scoreboard.GiveMoney(discordId, firstMatchingBet.Wager);
                    Bet updatedBet = BetBook.SwapBetAtId(firstMatchingBet.ID, discordId, winner, loser, nextScheduledMatch.Time, wager);
                    Scoreboard.TakeMoney(discordId, wager);
                    await SayInChannel(replyChannel, $"Bet changed: {updatedBet}");
                }
            }
        }

        string ResolvePlayerName(ulong discordId)
        {
            return Client.GetUser(discordId).Username;
        }

        async Task SayInChannel(SocketTextChannel channel, string message)
        {
            Console.WriteLine("[DISCORD] > \"{0}\"", message);

            await channel.SendMessageAsync(message);
        }

        async Task SayInMainChannel(string message)
        {
            await SayInChannel(MainChannel, message);
        }

        public async Task ResolveBetsAndNotifyForMatchAsync(FinishedMatch match)
        {
            var matchingBets = BetBook.GetBetsForMatch(match);
            var matchingPreds = BetBook.GetPredictionsForMatch(match);
            var winningBets = matchingBets.Where(bet => match.Victor.Equals(bet.Victor) && match.Loser.Equals(bet.Loser)).ToList();
            var losingBets = matchingBets.Where(bet => match.Loser.Equals(bet.Victor) && match.Victor.Equals(bet.Loser)).ToList();
            var rightPredictions = matchingPreds.Where(pred => match.Victor.Equals(pred.Victor) && match.Loser.Equals(pred.Loser)).ToList();
            var wrongPredictions = matchingPreds.Where(pred => match.Loser.Equals(pred.Victor) && match.Victor.Equals(pred.Loser)).ToList();

            if (winningBets.Count() + losingBets.Count() + rightPredictions.Count() + wrongPredictions.Count() > 0)
            {
                string betNotifyMsg = $"Results are in!\n{match.Victor} beat {match.Loser}.\n";
                // Evan, Jake, and Ian predicted C9 would win, which was correct. Basil and Andrew predicted DIG would win, which was incorrect.
                // Evan won $10 by betting on C9!

                // Notify correct predictions in condensed list
                if (rightPredictions.Count() > 0)
                {
                    string rightPredictionMsg = "";
                    for (int i = 0; i < rightPredictions.Count(); i++)
                    {
                        // Award player for correct prediction
                        Scoreboard.AwardCorrectPrediction(rightPredictions[i].DiscordID);
                        // Find the corresponding prediction from the active prediction list and remove it
                        BetBook.RemovePredictonWithId(rightPredictions[i].ID);

                        if (i > 0 && i + 1 == rightPredictions.Count()) // If this is the last one in a list of multiple, put 'and' before the user
                        {
                            rightPredictionMsg += $" and <@{rightPredictions[i].DiscordID}>"; // Print last user
                        }
                        else
                        {
                            if (i > 0) // If this isn't the first one in a list of multiple, put a comma before the next user
                                rightPredictionMsg += ", ";

                            rightPredictionMsg += $"<@{rightPredictions[i].DiscordID}>"; // Print next user
                        }
                    }
                    rightPredictionMsg += $" predicted that {match.Victor} would win, which was correct! (+${Prediction.AWARD})\n";
                    betNotifyMsg += rightPredictionMsg;
                }

                // Notify incorrect predictions in condensed list
                if (wrongPredictions.Count() > 0)
                {
                    string wrongPredictionMsg = "";
                    for (int i = 0; i < wrongPredictions.Count(); i++)
                    {
                        // Penalize player for incorrect prediction
                        Scoreboard.AwardIncorrectPrediction(wrongPredictions[i].DiscordID);
                        // Find the corresponding prediction from the active prediction list and remove it
                        BetBook.RemovePredictonWithId(wrongPredictions[i].ID);

                        if (i > 0 && i + 1 == wrongPredictions.Count()) // If this is the last one in a list of multiple
                        {
                            wrongPredictionMsg += $" and <@{wrongPredictions[i].DiscordID}>";
                        }
                        else
                        {
                            if (i > 0) // If this isn't the first one
                                wrongPredictionMsg += ", ";

                            wrongPredictionMsg += $"<@{wrongPredictions[i].DiscordID}>";
                        }
                    }
                    wrongPredictionMsg += $" predicted that {match.Loser} would win, which was incorrect.\n";
                    betNotifyMsg += wrongPredictionMsg;
                }

                // Notify Bets
                foreach (Bet wonBet in winningBets)
                {
                    // Award player for correct bet
                    Scoreboard.GiveMoney(wonBet.DiscordID, wonBet.Winnings);
                    // Find corresponding Bet and remove it
                    BetBook.RemoveBetWithId(wonBet.ID);
                    betNotifyMsg += $"<@{wonBet.DiscordID}> won {wonBet.Winnings:c} by betting on {match.Victor}!\n";
                }
                foreach (Bet lostBet in losingBets)
                {
                    // Find corresponding Bet and remove it
                    BetBook.RemoveBetWithId(lostBet.ID);
                    betNotifyMsg += $"<@{lostBet.DiscordID}> lost {lostBet.Wager:c} by betting on {match.Loser}.\n";
                }

                await SayInMainChannel(betNotifyMsg);
            }
        }

        Task DiscordReadyHandler()
        {
            if (!started)
            {
                MainChannel = Client.GetChannel(_DEBUG ? ID_TESTCHANNEL : ID_MAINCHANNEL) as SocketTextChannel;
                Task.Run(StartScorekeeping);
                started = true;
            }
            return Task.CompletedTask;
        }

        public async Task Start()
        {
            Client = new DiscordSocketClient();

            Client.Connected += () =>
            {
                Console.WriteLine("Bot logged in as {0}", Client.CurrentUser.Username);
                return Task.CompletedTask;
            };

            Client.Log += (msg) =>
            {
                Console.WriteLine("[DISCORD] {0}", msg.Message);
                return Task.CompletedTask;
            };

            Client.MessageReceived += ProcessMessage;
            Client.Ready += DiscordReadyHandler;

            await Client.LoginAsync(Discord.TokenType.Bot, DISCORD_TOKEN);
            await Client.StartAsync();
        }

        async Task StartScorekeeping()
        {
            if (_DEBUG)
                await SayInMainChannel("Bot started. Test mode enabled.");

            while (true)
            {
                // Only update Fri, Sat, and Sunday between 4AM and 10PM
                DayOfWeek dayOfWeek = DateTime.Now.DayOfWeek;
                if ((dayOfWeek == DayOfWeek.Friday || dayOfWeek == DayOfWeek.Saturday || dayOfWeek == DayOfWeek.Sunday)
                    && DateTime.Now.Hour > 3 && DateTime.Now.Hour < 22)
                {
                    // Update Match Record and Schedule from Online
                    var newMatches = MatchRecord.GetNewMatches();
                    Schedule = Schedule.GetOnlineSchedule();
                    if (newMatches.Count > 0)
                    {
                        Console.WriteLine($"{newMatches.Count} new matches to process!");
                        newMatches.ForEach(async match => await ResolveBetsAndNotifyForMatchAsync(match));
                        Console.WriteLine("Done processing new matches.");
                    }
                    else
                    {
                        if (_DEBUG)
                            Console.WriteLine("(Debug) No new matches to process.");
                    }
                }
                await Task.Delay(LOOP_DELAY_SECONDS * 1000);
            }
        }
    }
}
