// LCSDiscordbot.cs
// LCS Discord Bot V1.1
// By Evan Greavu
// github.com/evancg37

using HtmlAgilityPack;
using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Discord.WebSocket;

namespace LCS_Discord_Bot
{
    class Program2
    {
        public async static Task Main(string[] args)
        {
            string path = Path.Join(Directory.GetCurrentDirectory(), "scoreboard.csv");
            Scorekeeper scorekeeper = new Scorekeeper(path);

            Console.WriteLine($"{DateTime.Now}: Starting Scorekeeper loop.");
            await scorekeeper.Start();
	    Console.WriteLine($"{DateTime.Now}: Exited Scorekeeper loop.");
        }
    }

    class Scorekeeper
    {
	const bool _DEBUG = false;
        const int LOOP_DELAY_SECONDS = 60; // Number of seconds to wait in match result check loop
        const int BETBUFFER_MINS = 12; // Number of minutes past the hour of a scheduled match to accept bets
        const ulong ID_MAINCHANNEL = 900000000000000000; // Redacted
        const ulong ID_TESTCHANNEL = 910000000000000000; // Redacted 
        const string DISCORD_TOKEN = ""; // Redacted
	static Regex MessageParseRegex = new Regex(@"!([\w\d\s]+)");

        DiscordSocketClient Client;
        SocketTextChannel MainChannel;
        Scoreboard Scoreboard;
        MatchRecord MatchRecord;
        Schedule Schedule;
        List<Bet> Bets;
        List<Prediction> Predictions;
        string SavePath = "lcsbets.csv";
        bool started = false;

        public Scorekeeper()
        {
            Scoreboard = new Scoreboard();
            MatchRecord = MatchRecord.GetOnlineMatchRecord();
            Schedule = Schedule.GetOnlineSchedule();
            Bets = new List<Bet>();
            Predictions = new List<Prediction>();
        }

        public Scorekeeper(string path)
        {
            SavePath = path;
            if (File.Exists(SavePath))
                Scoreboard = Scoreboard.FromFile(SavePath);
            else
            {
                Scoreboard = new Scoreboard();
                Scoreboard.SaveToFile(SavePath);
            }
            MatchRecord = MatchRecord.GetOnlineMatchRecord();
            Schedule = Schedule.GetOnlineSchedule();
            Bets = new List<Bet>();
            Predictions = new List<Prediction>();
        }

        public Scorekeeper(Scoreboard scores) : this()
        {
            Scoreboard = scores;
        }

        public Scorekeeper(IEnumerable<Bet> fakeBets, IEnumerable<Prediction> fakePredictions, Scoreboard scoreboard)
        {
            Scoreboard = scoreboard;
            Schedule = Schedule.GetOnlineSchedule();
            Bets = fakeBets.ToList();
            Predictions = fakePredictions.ToList();
            MatchRecord = new MatchRecord();
        }

        public async Task ProcessMessage(SocketMessage message)
        {
            if (message.Content.StartsWith('!'))
            {
                Console.WriteLine("Possible command received: {0}", message.Content);
                var tryMatch = MessageParseRegex.Match(message.Content);
                if (tryMatch.Success)
                {
                    var words = tryMatch.Groups[1].Value.Split(' ');
                    string command = words[0];
                    var args = words.TakeLast(words.Count() - 1);
                    Console.WriteLine("Attemtping to interpret command: {0} withs args {1}", command, args);
                    await ProcessCommand(message.Author.Id, message.Channel as SocketTextChannel, command, args.ToList());
                }
            }
        }

        public async Task ProcessCommand(ulong discordId, SocketTextChannel replyChannel, string command, IList<string> args)
        {
            if (!Scoreboard.ContainsKey(discordId))
            {
                string name = ResolvePlayerName(discordId);
                Scoreboard[discordId] = new PlayerScore(discordId, name);
                Scoreboard.SaveToFile(SavePath);
                Console.WriteLine($"New player registered: {name} (DiscordID {discordId})");
            }

            command = command.ToLower().Trim();
            switch (command)
            {
                // Singlet commands
                case "score":
                case "money":
                case "myscore":
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
                    break;
                case "scores":
                case "allscores":
                case "scoreboard":
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
                    break;
                case "games":
                case "upcoming":
                case "schedule":
                    var futureGames = Schedule.Where(match => DateTime.Now < match.ScheduledTime + TimeSpan.FromMinutes(10));
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
                    break;
                case "mybets":
                case "bets":
                    var myBets = Bets.Where(bet => bet.DiscordID == discordId).ToList();
                    var myPredictions = Predictions.Where(pred => pred.DiscordID == discordId).ToList();
                    if (myBets.Count + myPredictions.Count == 0)
                        await SayInChannel(replyChannel, "You do not have any active bets or predictions.");
                    else
                    {
                        string betsString = "";
                        if (myBets.Count > 0)
                        {
                            betsString += "Your active bets:\n";
                            myBets.ForEach(bet => betsString += $"{bet.Wager:c} on {bet.Victor} beating {bet.Loser}\n");
                        }
                        if (myPredictions.Count > 0)
                        {
                            betsString += "Your predictions:\n";
                            myPredictions.ForEach(pred => betsString += $"{pred.Victor} will beat {pred.Loser}\n");
                        }
                        await SayInChannel(replyChannel, betsString);
                    }
                    break;
                case "allbets":
                    if (Bets.Count + Predictions.Count == 0)
                        await SayInChannel(replyChannel, "There are no active bets or predictions.");
                    else
                    {
                        string betString = "";
                        if (Bets.Count > 0)
                        {
                            betString += "All active bets:\n";
                            Bets.ForEach(bet => betString += $"{ResolvePlayerName(bet.DiscordID)} bet {bet.Wager} on {bet.Victor} beating {bet.Loser}.\n");
                        }
                        if (Predictions.Count > 0)
                        {
                            betString += "Active predictions:\n";
                            Predictions.ForEach(pred => betString += $"{ResolvePlayerName(pred.DiscordID)} thinks {pred.Victor} will beat {pred.Loser}.\n");
                        }
                        await SayInChannel(replyChannel, betString);
                    }
                    break;
                // Parameterized commands
                case "nextgame":
                case "next":
                    if (args.Count() == 0)
                        await SayInChannel(replyChannel, "Enter a team to check the next game for (!next <Team>) or a pair of teams to find their next match (!next <Team1> <Team2>)");
                    else if (args.Count() > 2)
                        await SayInChannel(replyChannel, "Usage: !next <Team> or !next <Team1> <Team2>");
                    else if (args.Count() == 1)
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
                    else // Checking for match between two teams
                    {
                        LCSTeam check1 = LCSTeam.ParseTeam(args[0]);
                        if (check1.ID == LCSTeam.TeamID.Unknown)
                        {
                            await SayInChannel(replyChannel, $"Unknown LCS team '{args[0]}'");
                            break;
                        }
                        LCSTeam check2 = LCSTeam.ParseTeam(args[1]);
                        if (check2.ID == LCSTeam.TeamID.Unknown)
                        {
                            await SayInChannel(replyChannel, $"Unknown LCS team '{args[1]}'");
                            break;
                        }
                        var nextMatchup = Schedule.GetNextMatchForMatchup(check1, check2);
                        if (nextMatchup == null)
                            await SayInChannel(replyChannel, $"{check1} and {check2} are not playing each other for the rest of the season.");
                        else
                            await SayInChannel(replyChannel, $"{check1} next plays {check2} at {nextMatchup.ScheduledTime:h:mm tt M/dd}");
                    }
                    break;
                case "predict":
                    if (args.Count() == 0)
                        await SayInChannel(replyChannel, "Enter a team to predict. (!predict <Team>)");
                    else if (args.Count() != 1)
                        await SayInChannel(replyChannel, "Usage: !predict <Team>");
                    else
                    {
                        LCSTeam team_pred = LCSTeam.ParseTeam(args[0]);
                        if (team_pred.ID == LCSTeam.TeamID.Unknown)
                        {
                            await SayInChannel(replyChannel, $"Unknown LCS team '{args[0]}'");
                        }
                        else
                        {
                            var nextScheduledMatch_pred = Schedule.GetNextMatchForTeam(team_pred);
                            if (nextScheduledMatch_pred == null)
                            {
                                await SayInChannel(replyChannel, $"Team {team_pred} has no games left in the season.");
                            }
                            else if (DateTime.Now > nextScheduledMatch_pred.ScheduledTime + TimeSpan.FromMinutes(BETBUFFER_MINS)) // If next game is within this hour
                            {
                                await SayInChannel(replyChannel, $"{team_pred} is currently playing! ({nextScheduledMatch_pred}) " +
                                    $"Wait until afer the current match to make a prediction.");
                            }
                            else
                            {
                                LCSTeam loser;
                                if (nextScheduledMatch_pred.Team1.Equals(team_pred))
                                    loser = nextScheduledMatch_pred.Team2;
                                else
                                    loser = nextScheduledMatch_pred.Team1;

                                // Update existing bets
                                bool updatedPred = false;
                                for (int i = 0; i < Predictions.Count; i++)
                                {
                                    if (Predictions[i].DiscordID == discordId) // Of my predictions...
                                    {
                                        if (Predictions[i].Victor.Equals(team_pred) && Predictions[i].Loser.Equals(loser)) // Predicting same victory combo
                                        {
                                            await SayInChannel(replyChannel, $"You already predicted that {Predictions[i].Victor} will beat {Predictions[i].Loser} ({nextScheduledMatch_pred})");
                                            updatedPred = true;
                                            break;
                                        }
                                        else if (Predictions[i].Loser.Equals(team_pred) && Predictions[i].Victor.Equals(loser)) // Betting on opposite victory combo
                                        {
                                            Predictions[i] = new Prediction(team_pred, loser, discordId); // Create new Bet
                                            await SayInChannel(replyChannel, $"Prediction changed: {Predictions[i].Victor} will beat {Predictions[i].Loser} ({nextScheduledMatch_pred})");
                                            updatedPred = true;
                                            break;
                                        }
                                    }
                                }

                                if (!updatedPred) // Create new bet if no bets were modified
                                {
                                    Prediction pred = new Prediction(team_pred, loser, discordId);
                                    Predictions.Add(pred);
                                    await SayInChannel(replyChannel, $"Prediction made: {pred.Victor} will beat {pred.Loser} ({nextScheduledMatch_pred.ScheduledTime:ddd M/d h:mm tt})");
                                }

                            }
                        }
                    }
                    break;
                case "placebet":
                case "makebet":
                case "bet":
                    // Parse and verify arguments
                    if (args.Count() != 2)
                    {
                        await SayInChannel(replyChannel, "Usage: !bet <Team> <Wager>");
                        break;
                    }
                    decimal wager;
                    int argWagerIndex = 0;
                    if (!decimal.TryParse(args[argWagerIndex].Trim('$'), out wager))
                    {
                        argWagerIndex = 1;
                        if (!decimal.TryParse(args[argWagerIndex].Trim('$'), out wager))
                        {
                            await SayInChannel(replyChannel, "Please enter an amount of money. (!bet <Team> <Wager>)");
                            break;
                        }
                    }
                    if (wager <= 0)
                    {
                        await SayInChannel(replyChannel, "Wager must be greater than $0.");
                        break;
                    }
                    int argTeamIndex = argWagerIndex == 0 ? 1 : 0;
                    LCSTeam team = LCSTeam.ParseTeam(args[argTeamIndex]);
                    if (team.ID == LCSTeam.TeamID.Unknown)
                    {
                        await SayInChannel(replyChannel, $"Unknown LCS team '{args[argTeamIndex]}'");
                        break;
                    }
                    // Get match timing
                    var nextScheduledMatch = Schedule.GetNextMatchForTeam(team);
                    if (nextScheduledMatch == null)
                    {
                        await SayInChannel(replyChannel, $"Team {team} has no games left in the season.");
                    }
                    else if (DateTime.Now > nextScheduledMatch.ScheduledTime + TimeSpan.FromMinutes(BETBUFFER_MINS)) // If next game is within this hour
                    {
                        await SayInChannel(replyChannel, $"{team} is currently playing! ({nextScheduledMatch}) " +
                            $"Wait until afer the current match to place a bet.");
                    }
                    else // Timing looks good
                    {
                        LCSTeam loser;
                        if (nextScheduledMatch.Team1.Equals(team))
                            loser = nextScheduledMatch.Team2;
                        else
                            loser = nextScheduledMatch.Team1;

                        // Update existing bets
                        bool updatedBet = false;
                        for (int i = 0; i < Bets.Count; i++)
                        {
                            if (Bets[i].DiscordID == discordId) // Of my bets...
                            {
                                if (Bets[i].Victor.Equals(team) && Bets[i].Loser.Equals(loser)) // Betting on same victory combo
                                {
                                    if (Bets[i].Wager != wager) // Change wager for same victory combo
                                    {
                                        if (Scoreboard[discordId].Money + Bets[i].Wager - wager < 0)
                                        {
                                            await SayInChannel(replyChannel, $"You can't change your bet by that much. You have {Scoreboard[discordId].Money:c}.");
                                            updatedBet = true;
                                            break;
                                        }
                                        else
                                        {
                                            Scoreboard[discordId].Money += Bets[i].Wager - wager; // Adjust player money to match wager change
                                            Bets[i] = new Bet(team, loser, wager, discordId);
                                            await SayInChannel(replyChannel, $"Bet changed: {Bets[i].Wager:c} on {Bets[i].Victor} beating {Bets[i].Loser} ({nextScheduledMatch.ScheduledTime:ddd M/d h:mm tt})");
                                            updatedBet = true;
                                            Scoreboard.SaveToFile(SavePath);
                                            break;
                                        }
                                    }
                                    else // Same wager and victory combo
                                    {
                                        await SayInChannel(replyChannel, $"You already bet {Bets[i].Wager:c} on {Bets[i].Victor} (vs {Bets[i].Loser} {nextScheduledMatch.ScheduledTime:ddd M/d h:mm tt})");
                                        updatedBet = true;
                                        break;
                                    }
                                }
                                else if (Bets[i].Loser.Equals(team) && Bets[i].Victor.Equals(loser)) // Betting on opposite victory combo
                                {
                                    if (Scoreboard[discordId].Money + Bets[i].Wager - wager < 0)
                                    {
                                        await SayInChannel(replyChannel, $"You can't change your bet to that. You have {Scoreboard[discordId].Money:c}.");
                                        updatedBet = true;
                                        break;
                                    }
                                    else
                                    {
                                        Scoreboard[discordId].Money += Bets[i].Wager - wager; // Adjust player money to match wager change
                                        Bets[i] = new Bet(team, loser, wager, discordId); // Create new Bet
                                        await SayInChannel(replyChannel, $"Bet changed: {Bets[i].Wager:c} on {Bets[i].Victor} beating {Bets[i].Loser} ({nextScheduledMatch.ScheduledTime:ddd M/d h:mm tt})");
                                        updatedBet = true;
                                        Scoreboard.SaveToFile(SavePath);
                                        break;
                                    }
                                }
                            }
                        }

                        if (!updatedBet) // Create new bet if no bets were modified
                        {
                            if (Scoreboard[discordId].Money - wager < 0)
                            {
                                await SayInChannel(replyChannel, $"You can't bet that much. You have {Scoreboard[discordId].Money:c}.");
                                updatedBet = true;
                                break;
                            }
                            else
                            {
                                Bet bet = new Bet(team, loser, wager, discordId);
                                Bets.Add(bet);
                                Scoreboard.TakeMoney(discordId, wager);
                                Scoreboard[discordId].BetCount += 1;
                                Scoreboard.SaveToFile(SavePath);
                                await SayInChannel(replyChannel, $"Bet placed: {wager:c} on {team} beating {loser} ({nextScheduledMatch.ScheduledTime:ddd M/d h:mm tt})");
                            }
                        }
                    }
                    break;
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

        public async Task ResolveBetsAndNotifyForMatchAsync(MatchResult match)
        {
            var winningBets = Bets.Where(bet => match.Victor.Equals(bet.Victor) && match.Loser.Equals(bet.Loser)).ToList();
            var losingBets = Bets.Where(bet => match.Loser.Equals(bet.Victor) && match.Victor.Equals(bet.Loser)).ToList();
            var rightPredictions = Predictions.Where(pred => match.Victor.Equals(pred.Victor) && match.Loser.Equals(pred.Loser)).ToList();
            var wrongPredictions = Predictions.Where(pred => match.Loser.Equals(pred.Victor) && match.Victor.Equals(pred.Loser)).ToList();
            if (winningBets.Count() + losingBets.Count() + rightPredictions.Count() + wrongPredictions.Count() > 0)
            {
                string message = $"Results are in!\n{match.Victor} beat {match.Loser}.\n";
                // Evan, Jake, and Ian predicted C9 would win, which was correct. Basil and Andrew predicted DIG would win, which was incorrect.
                // Evan won $10 by betting on C9!

                // Notify correct predictions
                if (rightPredictions.Count() > 0)
                {
                    string rightPredictionLine = "";
                    for (int i = 0; i < rightPredictions.Count(); i++)
                    {
                        // Award player for correct prediction
                        Scoreboard.GiveCorrectPrediction(rightPredictions[i].DiscordID);
                        // Find the corresponding prediction from the active prediction list and remove it
                        Predictions.RemoveAt(Predictions.FindIndex(currentPred => currentPred.DiscordID == rightPredictions[i].DiscordID
                            && currentPred.Victor.Equals(rightPredictions[i].Victor) && currentPred.Loser.Equals(rightPredictions[i].Loser)));
                        
                        if (i > 0 && i + 1 == rightPredictions.Count()) // If this is the last one in a list of multiple
                        {
                            rightPredictionLine += $" and <@{rightPredictions[i].DiscordID}>";
                        }
                        else
                        {
                            if (i > 0) // If this isn't the first one
                                rightPredictionLine += ", ";

                            rightPredictionLine += $"<@{rightPredictions[i].DiscordID}>";
                        }
                    }
                    rightPredictionLine += $" predicted that {match.Victor} would win, which was correct! (+${Prediction.AWARD})\n";
                    message += rightPredictionLine;
                }

                // Notify incorrect predictions
                if (wrongPredictions.Count() > 0)
                {
                    string wrongPredictionLine = "";
                    for (int i = 0; i < wrongPredictions.Count(); i++)
                    {
                        // Penalize player for incorrect prediction
                        Scoreboard.GiveIncorrectPrediction(wrongPredictions[i].DiscordID);
                        // Find the corresponding prediction from the active prediction list and remove it
                        Predictions.RemoveAt(Predictions.FindIndex(currentPred => currentPred.DiscordID == wrongPredictions[i].DiscordID
                            && currentPred.Victor.Equals(wrongPredictions[i].Victor) && currentPred.Loser.Equals(wrongPredictions[i].Loser)));

                        if (i > 0 && i + 1 == wrongPredictions.Count()) // If this is the last one in a list of multiple
                        {
                            wrongPredictionLine += $" and <@{wrongPredictions[i].DiscordID}>";
                        }
                        else
                        {
                            if (i > 0) // If this isn't the first one
                                wrongPredictionLine += ", ";

                            wrongPredictionLine += $"<@{wrongPredictions[i].DiscordID}>";
                        }
                    }
                    wrongPredictionLine += $" predicted that {match.Loser} would win, which was incorrect.\n";
                    message += wrongPredictionLine;
                }

                // Notify Bets
                foreach (var bet in winningBets)
                {
                    // Award player for correct bet
                    Scoreboard.GiveMoney(bet.DiscordID, bet.Winnings);
                    // Find corresponding Bet and remove it
                    Bets.RemoveAt(Bets.FindIndex(placedBet => placedBet.DiscordID == bet.DiscordID 
                        && placedBet.Victor.Equals(bet.Victor) && placedBet.Loser.Equals(bet.Loser)));
                    message += $"<@{bet.DiscordID}> won {bet.Winnings:c} by betting on {match.Victor}!\n";
                }
                foreach (var bet in losingBets)
                {
                    // Find corresponding Bet and remove it
                    Bets.RemoveAt(Bets.FindIndex(placedBet => placedBet.DiscordID == bet.DiscordID
                        && placedBet.Victor.Equals(bet.Victor) && placedBet.Loser.Equals(bet.Loser)));
                    message += $"<@{bet.DiscordID}> lost {bet.Wager:c} by betting on {match.Loser}.\n";
                }

                Scoreboard.SaveToFile(SavePath);
                await SayInMainChannel(message);
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
                        Console.WriteLine($"{newMatches.Count} new matches to process.");
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

    class LCSTeam
    {
        public enum TeamID { TSM, TL, C9, CLG, DIG, _100T, FLY, GG, EG, IMT, Unknown=-1 }

        public string Name { get { return ID == TeamID._100T ? "100T" : ID.ToString(); } private set { } }
        public readonly TeamID ID;

        public LCSTeam(TeamID teamID)
        {
            ID = teamID;
        }

        public static LCSTeam ParseTeam(string s)
        {
            TeamID id;
            s = s.ToLower().Trim();
            switch (s)
            {
                case "tsm":
                case "teamsolomid":
                    id = TeamID.TSM;
                    break;
                case "tl":
                case "liquid":
                case "teamliquid":
                    id = TeamID.TL;
                    break;
                case "100t":
                case "100":
                case "100thieves":
                case "10t":
                case "thieves":
                    id = TeamID._100T;
                    break;
                case "dig":
                case "dignitas":
                    id = TeamID.DIG;
                    break;
                case "c9":
                case "cl9":
                case "cloud9":
                case "cloudnine":
                    id = TeamID.C9;
                    break;
                case "flyquest":
                case "fly":
                case "flyq":
                    id = TeamID.FLY;
                    break;
                case "gg":
                case "golden":
                case "goldenguardians":
                case "ggs":
                    id = TeamID.GG;
                    break;
                case "clg":
                case "counterlogicgaming":
                case "counterlogic":
                case "counter":
                    id = TeamID.CLG;
                    break;
                case "imt":
                case "immortals":
                    id = TeamID.IMT;
                    break;
                case "eg":
                case "evil":
                case "geniuses":
                case "evilgeniuses":
                    id = TeamID.EG;
                    break;
                default:
                    id = TeamID.Unknown;
                    break;
            }
            return new LCSTeam(id);
        }

        public override bool Equals(object obj)
        {
            return ID == (obj as LCSTeam).ID;
        }

        public override string ToString()
        {
            return Name;
        }

    }

    class Bet
    {
        public readonly LCSTeam Victor;
        public readonly LCSTeam Loser;
        public readonly decimal Wager;
        public readonly ulong DiscordID;
        public decimal Winnings { get { return 2 * Wager; } }

        public Bet(LCSTeam winner, LCSTeam loser, decimal wager, ulong discordId)
        {
            Victor = winner;
            Loser = loser;
            Wager = wager;
            DiscordID = discordId;
        }
    }

    class Prediction
    {
        public const decimal AWARD = 10;
        public readonly LCSTeam Victor;
        public readonly LCSTeam Loser;
        public readonly ulong DiscordID;

        public Prediction(LCSTeam winner, LCSTeam loser, ulong discordId)
        {
            Victor = winner;
            Loser = loser;
            DiscordID = discordId;
        }
    }

    class FutureMatch
    {
        public readonly LCSTeam Team1;
        public readonly LCSTeam Team2;
        public readonly DateTime ScheduledTime;
        public FutureMatch(LCSTeam t1, LCSTeam t2, DateTime time)
        {
            Team1 = t1;
            Team2 = t2;
            ScheduledTime = time;
        }

        public override string ToString()
        {
            return $"{Team1} vs {Team2} - {ScheduledTime:ddd M/dd h:mm tt}";
        }
    }

    class MatchResult
    {
        public readonly LCSTeam Victor;
        public readonly LCSTeam Loser;

        public MatchResult(LCSTeam winner, LCSTeam loser)
        {
            Victor = winner;
            Loser = loser;
        }
    }

    class MatchRecord : List<MatchResult>
    {
        const string URL_RECORD = "https://lol.gamepedia.com/LCS/2020_Season/Summer_Season";

        /// <summary>
        /// Updates the match record to the online record, and returns new matches for resolving bets and predictions.
        /// </summary>
        /// <returns>A list of MatchResults for use in conjunction with the Bet Resolution Notifier</returns>
        public List<MatchResult> GetNewMatches()
        {
            var matchRecord = GetOnlineMatchRecord();
            var newMatches = matchRecord.TakeLast(matchRecord.Count - Count).ToList();
            AddRange(newMatches);
            return newMatches;
        }

        /// <summary>
        /// Downloads the list of occurred LCS matches from lol.gamepedia.com
        /// </summary>
        /// <returns>A MatchRecord object containing the list of occurred LCS matches</returns>
        public static MatchRecord GetOnlineMatchRecord()
        {
            // Get HTML document
            string page_content;
            using (WebClient client = new WebClient())
            {
                page_content = client.DownloadString(URL_RECORD);
            }
            HtmlDocument html = new HtmlDocument();
            html.LoadHtml(page_content);
            HtmlNodeCollection raw_matches = html.DocumentNode.SelectNodes("//tr[contains(@class, 'mdv-allweeks mdv-week')]");

            // Get occurred mathes from HTML document
            MatchRecord record = new MatchRecord();
            foreach (HtmlNode node in raw_matches)
            {
                if (node.InnerLength > 1000) // Match has occurred
                {
                    HtmlNodeCollection teams = node.SelectNodes(".//span[@class='teamname']");
                    LCSTeam team1 = LCSTeam.ParseTeam(teams[0].InnerText);
                    LCSTeam team2 = LCSTeam.ParseTeam(teams[1].InnerText);

                    if (!string.IsNullOrEmpty(team1.Name) && !string.IsNullOrEmpty(team2.Name)) // Both teams in Match detected
                    {
                        HtmlNode win = node.SelectSingleNode(".//td[@class='md-winner']");
                        if (win != null) // Winner was found for this match
                        {
                            LCSTeam winner = LCSTeam.ParseTeam(win.InnerText);
                            if (winner.Equals(team1) || winner.Equals(team2)) // Make sure we detected the winning team correctly
                            {
                                LCSTeam other;
                                if (winner.Equals(team1))
                                    other = team2;
                                else
                                    other = team1;

                                record.Add(new MatchResult(winner, other));
                            }
                        }
                    }
                }
            }
            return record;
        }

        public MatchRecord()
        {
            
        }
    }

    public class PlayerScore
    {
        public const decimal STARTING_MONEY = 100;
        public ulong DiscordID;
        public string Name;
        public int PredictionsRight;
        public int PredictionsWrong;
        public int BetCount;
        public decimal Money;
        public decimal MoneyFromPredictions {  get { return PredictionsRight * Prediction.AWARD; } }
        public decimal MoneyFromBets { get { return Money - MoneyFromPredictions - STARTING_MONEY; } }

        public PlayerScore(ulong discordId, string name)
        {
            DiscordID = discordId;
            Name = name;
            PredictionsRight = 0;
            PredictionsWrong = 0;
            BetCount = 0;
            Money = STARTING_MONEY;
        }

        public PlayerScore(ulong discordId, string name, int pRight=0, int pWrong=0, int betCount=0, decimal money=0)
        {
            DiscordID = discordId;
            Name = name;
            PredictionsRight = pRight;
            PredictionsWrong = pWrong;
            BetCount = betCount;
            Money = money;
        }

        public int CompareTo(PlayerScore item2)
        {
            return item2.Money.CompareTo(Money);
        }
    }

    class Schedule : List<FutureMatch>
    {
        const string URL_SCHEDULE = "https://lol.gamepedia.com/Special:RunQuery/MatchCalendarExport?MCE%5B1%5D=LCS/2020%20Season/Summer%20Season&pfRunQueryFormName=MatchCalendarExport";
        const int FUTURE_BUFFER_MINS = 30;
        static Regex REGEX_MATCHSCHEDULE = new Regex(@"- ([\w\d]{1,3}) vs ([\w\d]{1,3}),(\d{4}),(\d{1,2}),(\d{1,2}),(\d{1,2}),(\d{1,2})");
        
        public static Schedule GetOnlineSchedule()
        {
            Schedule result = new Schedule();
            string page_content;
            using (WebClient client = new WebClient())
            {
                page_content = client.DownloadString(URL_SCHEDULE);
            }
            HtmlDocument html = new HtmlDocument();
            html.LoadHtml(page_content);
            HtmlNode raw_schedule = html.DocumentNode.SelectSingleNode("//div[@class='mw-parser-output']/pre"); // Select schedule block
            foreach (string line in raw_schedule.InnerText.Split('\n'))
            {
                Match regex_schedule = REGEX_MATCHSCHEDULE.Match(line);
                if (regex_schedule.Success)
                {
                    string t1 = regex_schedule.Groups[1].Value;
                    string t2 = regex_schedule.Groups[2].Value;
                    string year = regex_schedule.Groups[3].Value;
                    string month = regex_schedule.Groups[4].Value;
                    string day = regex_schedule.Groups[5].Value;
                    string hour = regex_schedule.Groups[6].Value;
                    string minute = regex_schedule.Groups[7].Value;
                    DateTime match_utctime = DateTime.Parse($"{month}/{day}/{year} {hour}:{minute}:00", CultureInfo.InvariantCulture);
                    DateTime match_time = match_utctime + TimeZoneInfo.Local.GetUtcOffset(match_utctime + TimeZoneInfo.Local.BaseUtcOffset);
                    LCSTeam team1 = LCSTeam.ParseTeam(t1);
                    LCSTeam team2 = LCSTeam.ParseTeam(t2);
                    FutureMatch scheduled_match = new FutureMatch(team1, team2, match_time);
                    result.Add(scheduled_match);
                }
            }
            if (result.Count == 0)
            {
                Console.WriteLine("Failed to parse schedule.");
                return null;
            }
            return result;
        }

        public FutureMatch GetNextMatchForTeam(LCSTeam team)
        {
            var nextMatchesForTeam = this.Where(match => DateTime.Now < match.ScheduledTime + TimeSpan.FromMinutes(FUTURE_BUFFER_MINS) && (match.Team1.Equals(team) || match.Team2.Equals(team)));
            if (nextMatchesForTeam.Count() == 0)
                return null;
            return nextMatchesForTeam.First();
        }

        public FutureMatch GetNextMatchForMatchup(LCSTeam team1, LCSTeam team2)
        {
            var nextMatchups = this.Where(match => 
                DateTime.Now < match.ScheduledTime + TimeSpan.FromMinutes(FUTURE_BUFFER_MINS) &&
                (match.Team1.Equals(team1) && match.Team2.Equals(team2) || 
                match.Team2.Equals(team1) && match.Team1.Equals(team2))
            );
            if (nextMatchups.Count() == 0)
                return null;
            return nextMatchups.First();
        }
    }

    public class Scoreboard : Dictionary<ulong, PlayerScore>
    {
        public void GiveMoney(ulong playerId, decimal money)
        {
            this[playerId].Money += money;
        }

        public void TakeMoney(ulong playerId, decimal money)
        {
            this[playerId].Money -= money;
        }

        public void GiveCorrectPrediction(ulong playerId)
        {
            this[playerId].PredictionsRight += 1;
            GiveMoney(playerId, Prediction.AWARD);
        }

        public void GiveIncorrectPrediction(ulong playerId)
        {
            this[playerId].PredictionsWrong += 1;
        }

        public Scoreboard(IEnumerable<PlayerScore> fakeScores)
        {
            foreach (PlayerScore score in fakeScores)
            {
                Add(score.DiscordID, score);
            }
        }

        public Scoreboard()
        {
        }

        public bool SaveToFile(string path) // CSV
        {
            if (Count > 0)
            {
                using (FileStream fileStream = new FileStream(path, FileMode.OpenOrCreate))
                {
                    foreach (var scoreEntry in this.ToArray())
                    {
                        // Serialize and save to file
                        string csvLine = $"{scoreEntry.Key},{scoreEntry.Value.Name},{scoreEntry.Value.Money},{scoreEntry.Value.PredictionsRight}," +
                            $"{scoreEntry.Value.PredictionsWrong},{scoreEntry.Value.BetCount}\n";

                        fileStream.Write(Encoding.UTF8.GetBytes(csvLine));
                    }
                }
                return true;
            }
            return false;
        }

        public static Scoreboard FromFile(string path) // CSV
        {
            if (File.Exists(path))
            {
                Scoreboard scoreboard = new Scoreboard();
                string[] lines = File.ReadAllLines(path);
                foreach (string line in lines)
                {
                    string[] columns = line.Split(',');
                    ulong playerId = ulong.Parse(columns[0]);
                    PlayerScore player = new PlayerScore(playerId, columns[1], money: decimal.Parse(columns[2]),
                        pRight: int.Parse(columns[3]), pWrong: int.Parse(columns[4]), betCount: int.Parse(columns[5]));
                    scoreboard.Add(playerId, player);
                }
                return scoreboard;
            } 
            else
            {
                throw new FileNotFoundException("Cannot load Scoreboard from file because the file does not exist.");
            }
        }
    }
}
