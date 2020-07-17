// LCS Discord Bot
// V1.2 
// Evan Greavu
// Scorekeeping.csv

using HtmlAgilityPack;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;

namespace LCSDiscordBot
{
    class Bet
    {
        public int ID { get; }
        public LCSTeam Victor { get; }
        public LCSTeam Loser { get; }
        public decimal Wager { get; }
        public ulong DiscordID { get; }
        public DateTime MatchTime { get; }
        public decimal Winnings { get { return 2 * Wager; } }

        public Bet(int id, ulong discordId, LCSTeam winner, LCSTeam loser, DateTime time, decimal wager)
        {
            ID = id;
            Victor = winner;
            Loser = loser;
            Wager = wager;
            DiscordID = discordId;
            MatchTime = time;
        }

        public override string ToString()
        {
            return $"{Wager:c} on {Victor} beating {Loser} {MatchTime:ddd h:mm tt MM/dd}";
        }
    }

    class Prediction
    {
        public const decimal AWARD = 10;
        public int ID { get; }
        public LCSTeam Victor { get; }
        public LCSTeam Loser { get; }
        public ulong DiscordID { get; }
        public DateTime MatchTime { get; }

        public Prediction(int id, ulong discordId, LCSTeam winner, LCSTeam loser, DateTime time)
        {
            ID = id;
            Victor = winner;
            Loser = loser;
            DiscordID = discordId;
            MatchTime = time;
        }

        public override string ToString()
        {
            return $"{Victor} will beat {Loser} {MatchTime:ddd h:mm tt MM/dd}";
        }
    }

    class ScheduledMatch
    {
        public LCSTeam Team1 { get; }
        public LCSTeam Team2 { get; }
        public DateTime Time { get; }
        public ScheduledMatch(LCSTeam t1, LCSTeam t2, DateTime time)
        {
            Team1 = t1;
            Team2 = t2;
            Time = time;
        }

        public override string ToString()
        {
            return $"{Team1} vs {Team2} - {Time:ddd M/dd h:mm tt}";
        }
    }

    class FinishedMatch
    {
        public LCSTeam Victor { get; }
        public LCSTeam Loser { get; }
        public DateTime Time { get; }

        public FinishedMatch(LCSTeam winner, LCSTeam loser, DateTime time)
        {
            Victor = winner;
            Loser = loser;
            Time = time;
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
        public decimal MoneyFromPredictions { get { return PredictionsRight * Prediction.AWARD; } }
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

        public PlayerScore(ulong discordId, string name, int pRight = 0, int pWrong = 0, int betCount = 0, decimal money = 0)
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

    class LCSTeam // Wraps a TeamID Enum into a more easily printed LCSTeam (100T's fault)
    {
        public enum TeamID { TSM, TL, C9, CLG, DIG, _100T, FLY, GG, EG, IMT, Unknown = -1 }

        public string Name { get { return ID == TeamID._100T ? "100T" : ID.ToString(); } private set { } }
        public TeamID ID { get; }

        public LCSTeam(TeamID teamID)
        {
            ID = teamID;
        }

        public static LCSTeam ParseTeam(string s)
        {
            switch (s.ToLower().Trim())
            {
                case "tsm":
                case "teamsolomid":
                    return new LCSTeam(TeamID.TSM);
                case "tl":
                case "liquid":
                case "teamliquid":
                    return new LCSTeam(TeamID.TL);
                case "100t":
                case "100":
                case "100thieves":
                case "10t":
                case "thieves":
                    return new LCSTeam(TeamID._100T);
                case "dig":
                case "dignitas":
                    return new LCSTeam(TeamID.DIG);
                case "c9":
                case "cl9":
                case "cloud9":
                case "cloudnine":
                    return new LCSTeam(TeamID.C9);
                case "flyquest":
                case "fly":
                case "flyq":
                    return new LCSTeam(TeamID.FLY);
                case "gg":
                case "golden":
                case "goldenguardians":
                case "ggs":
                    return new LCSTeam(TeamID.GG);
                case "clg":
                case "counterlogicgaming":
                case "counterlogic":
                case "counter":
                    return new LCSTeam(TeamID.CLG);
                case "imt":
                case "immortals":
                    return new LCSTeam(TeamID.IMT);
                case "eg":
                case "evil":
                case "geniuses":
                case "evilgeniuses":
                    return new LCSTeam(TeamID.EG);

                // Could not parse Team
                default:
                    return new LCSTeam(TeamID.Unknown);
            }
            
        }

        public override bool Equals(object obj)
        {
            return ID == (obj as LCSTeam).ID;
        }

        public override int GetHashCode()
        {
            return (int)ID;
        }

        public override string ToString()
        {
            return Name;
        }

    }

    /// <summary>
    /// Saves and tracks Bets and Predictions. Provides methods for the creation and updating of Bets and Predictions.
    /// </summary>
    class BetBook
    {
        const int RECENTMATCH_DAYS = 7;

        public int BetCount { get { return bets.Count; } }
        public int PredictionCount { get { return predictions.Count; } }
        public int AllCount { get { return BetCount + PredictionCount; } }

        private List<Bet> bets;
        private List<Prediction> predictions;
        private readonly string savePath = "bets.csv";
        private int newBetId;
        private int newPredId;

        public BetBook()
        {
            bets = new List<Bet>();
            predictions = new List<Prediction>();
            newBetId = 0;
            newPredId = 0;
        }

        BetBook(IList<Bet> bets, IList<Prediction> predictions)
        {
            this.bets = bets as List<Bet>;
            this.predictions = predictions as List<Prediction>;
            newBetId = GetHighestBetId() + 1;
            newPredId = GetHighestPredId() + 1;
        }

        public List<Bet> GetBetsForPlayer(ulong discordId)
        {
            return bets.Where(bet => bet.DiscordID == discordId).ToList();
        }

        public List<Bet> GetBetsForPlayerAndMatch(ulong discordId, ScheduledMatch match)
        {
            return GetBetsForPlayer(discordId).Where(bet =>
            (bet.Victor.Equals(match.Team1) && bet.Loser.Equals(match.Team2))
            || (bet.Victor.Equals(match.Team2) && bet.Loser.Equals(match.Team1))).ToList();
        }

        public List<Prediction> GetPredictionsForPlayerAndMatch(ulong discordId, ScheduledMatch match)
        {
            return GetPredictionsForPlayer(discordId).Where(pred =>
            (pred.Victor.Equals(match.Team1) && pred.Loser.Equals(match.Team2))
            || (pred.Victor.Equals(match.Team2) && pred.Loser.Equals(match.Team1))).ToList();
        }

        public List<Bet> GetAllBets()
        {
            return bets.ToList();
        }

        public List<Prediction> GetPredictionsForPlayer(ulong discordId)
        {
            return predictions.Where(pred => pred.DiscordID == discordId).ToList();
        }

        public List<Prediction> GetAllPredictions()
        {
            return predictions.ToList();
        }

        /// <summary>
        /// Finds tracked bets corresponding to a specific LCS Match
        /// </summary>
        public List<Bet> GetBetsForMatch(FinishedMatch match)
        {
            return bets.Where(bet =>
              (bet.Victor.Equals(match.Victor) && bet.Loser.Equals(match.Loser))
              || (bet.Victor.Equals(match.Loser) && bet.Loser.Equals(match.Victor))
              && (bet.MatchTime > match.Time - TimeSpan.FromDays(7) && bet.MatchTime < match.Time + TimeSpan.FromDays(7))).ToList();
        }

        /// <summary>
        /// Finds tracked predictions corresponding to a specific LCS Match
        /// </summary>
        public List<Prediction> GetPredictionsForMatch(FinishedMatch match)
        {
            return predictions.Where(pred =>
              (pred.Victor.Equals(match.Victor) && pred.Loser.Equals(match.Loser))
              || (pred.Victor.Equals(match.Loser) && pred.Loser.Equals(match.Victor))
              && (pred.MatchTime > match.Time - TimeSpan.FromDays(7) && pred.MatchTime < match.Time + TimeSpan.FromDays(7))).ToList();
        }

        /// <summary>
        /// Create a bet and start tracking it. Optionally saves to file when this occurs.
        /// </summary>
        public Bet CreateBet(ulong discordId, LCSTeam victor, LCSTeam loser, DateTime time, decimal wager, bool save = true)
        {
            Bet bet = new Bet(newBetId++, discordId, victor, loser, time, wager);
            bets.Add(bet);
            if (save)
                SaveToFile(savePath);
            return bet;
        }

        /// <summary>
        /// Create a prediction and start tracking it. Optionally saves to file when this occurs.
        /// </summary>
        public Prediction CreatePrediction(ulong discordId, LCSTeam victor, LCSTeam loser, DateTime time, bool save = true)
        {
            Prediction pred = new Prediction(newPredId++, discordId, victor, loser, time);
            predictions.Add(pred);
            if (save)
                SaveToFile(savePath);
            return pred;
        }

        /// <summary>
        /// Remove a Prediction with a given ID. Optionally saves to file when this occurs.
        /// </summary>
        public void RemovePredictonWithId(int id, bool save = true)
        {
            int index = predictions.FindIndex(pred => pred.ID == id);
            if (index != -1)
                predictions.RemoveAt(index);
            if (save)
                SaveToFile(savePath);
        }

        /// <summary>
        /// Remove a Bet with a given ID. Optionally saves to file when this occurs.
        /// </summary>
        public void RemoveBetWithId(int id, bool save = true)
        {
            int index = bets.FindIndex(bet => bet.ID == id);
            if (index != -1)
                bets.RemoveAt(index);
            if (save)
                SaveToFile(savePath);
        }

        /// <summary>
        /// Swap out a tracked prediction with a replacement. Optionally saves to file when this occurs.
        /// </summary>
        public Prediction SwapPredictionAtId(int id, ulong discordId, LCSTeam victor, LCSTeam loser, DateTime time, bool save = true)
        {
            var newPred = new Prediction(id, discordId, victor, loser, time);
            RemovePredictonWithId(id);
            predictions.Add(newPred);
            if (save)
                SaveToFile(savePath);
            return newPred;
        }

        /// <summary>
        /// Swap out a tracked bet with a replacement. Optionally saves to file when this occurs.
        /// </summary>
        public Bet SwapBetAtId(int id, ulong discordId, LCSTeam victor, LCSTeam loser, DateTime time, decimal wager, bool save = true)
        {
            var newBet = new Bet(id, discordId, victor, loser, time, wager);
            RemoveBetWithId(id);
            bets.Add(newBet);
            if (save)
                SaveToFile(savePath);
            return newBet;
        }

        private int GetHighestBetId()
        {
            if (bets.Count > 0)
            {
                var sortedBets = bets.ToList();
                sortedBets.Sort((bet1, bet2) => bet2.ID.CompareTo(bet1.ID));
                return sortedBets.First().ID;
            }
            else
                return -1;
        }

        private int GetHighestPredId()
        {
            if (predictions.Count > 0)
            {
                var sortedPreds = predictions.ToList();
                sortedPreds.Sort((pred1, pred2) => pred2.ID.CompareTo(pred1.ID));
                return sortedPreds.First().ID;
            }
            else
                return -1;
        }

        public static BetBook FromFile(string path)
        {
            CSVHelper csv = new CSVHelper();
            List<Bet> loaded_bets = new List<Bet>();
            List<Prediction> loaded_predictions = new List<Prediction>();
            List<List<string>> csvFileData;
            try
            {
                csvFileData = csv.ReadCsvFile(path);
                foreach (var row in csvFileData)
                {
                    string rowCol0 = row[0].ToLower().Trim();
                    // p,4,163471259110342657,TSM,C9,07-11-2020
                    // type,predId,discordId,win,loss,date
                    if (rowCol0.Equals("p") && row.Count >= 6) // prediction row 
                    {
                        int predId = int.Parse(row[1]);
                        ulong discordId = ulong.Parse(row[2]);
                        LCSTeam victor = LCSTeam.ParseTeam(row[3]);
                        LCSTeam loser = LCSTeam.ParseTeam(row[4]);
                        DateTime matchTime = DateTime.ParseExact(row[5], "M/d HH:mm", CultureInfo.InvariantCulture);
                        loaded_predictions.Add(new Prediction(predId, discordId, victor, loser, matchTime));
                    }
                    // b,6,163471259110342657,TSM,C9,07-11-2020,100
                    // type,betId,discordId,win,loss,date,wager
                    else if (rowCol0.Equals("b") && row.Count >= 7) // bet row
                    {
                        int betId = int.Parse(row[1]);
                        ulong discordId = ulong.Parse(row[2]);
                        LCSTeam victor = LCSTeam.ParseTeam(row[3]);
                        LCSTeam loser = LCSTeam.ParseTeam(row[4]);
                        DateTime matchTime = DateTime.ParseExact(row[5], "M/d HH:mm", CultureInfo.InvariantCulture);
                        decimal wager = decimal.Parse(row[6]);
                        loaded_bets.Add(new Bet(betId, discordId, victor, loser, matchTime, wager));
                    }
                }
            }
            catch
            {
                Console.WriteLine($"Failed to load BetBook from {path}! BetBook will be empty.");
            }

            return new BetBook(loaded_bets, loaded_predictions);
        }

        public void SaveToFile(string path)
        {
            CSVHelper csv = new CSVHelper();
            List<IList<object>> data = new List<IList<object>>();
            foreach (var bet in bets)
            {
                List<object> columns = new List<object>
                {
                    "b",
                    bet.ID,
                    bet.DiscordID,
                    bet.Victor,
                    bet.Loser,
                    $"{bet.MatchTime:M/d HH:mm}",
                    bet.Wager
                };
                data.Add(columns);
            }
            foreach (var pred in predictions)
            {
                List<object> columns = new List<object>
                {
                    "p",
                    pred.ID,
                    pred.DiscordID,
                    pred.Victor,
                    pred.Loser,
                    $"{pred.MatchTime:M/d HH:mm}"
                };
                data.Add(columns);
            }
            try
            {
                csv.SaveToCsvFile(path, data);
            }
            catch
            {
                Console.WriteLine($"Failed to save object to {path}");
            }
        }
    }

    class MatchRecord : List<FinishedMatch>
    {
        const string URL_RECORD = "https://lol.gamepedia.com/LCS/2020_Season/Summer_Season";

        /// <summary>
        /// Updates the match record to the online record, and returns new matches for resolving bets and predictions.
        /// </summary>
        /// <returns>A list of MatchResults for use in conjunction with the Bet Resolution Notifier</returns>
        public List<FinishedMatch> GetNewMatches()
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

            // Get occurred matches from HTML document
            MatchRecord record = new MatchRecord();
            foreach (HtmlNode node in raw_matches)
            {
                if (node.InnerLength < 1000)
                    continue;

                HtmlNodeCollection teams = node.SelectNodes(".//span[@class='teamname']");
                LCSTeam team1 = LCSTeam.ParseTeam(teams[0].InnerText);
                LCSTeam team2 = LCSTeam.ParseTeam(teams[1].InnerText);
                if (team1.ID == LCSTeam.TeamID.Unknown || team2.ID == LCSTeam.TeamID.Unknown)
                {
                    continue; // Teams could not be identified
                }

                HtmlNode win = node.SelectSingleNode(".//td[@class='md-winner']");
                if (win == null)
                    continue; // Winner not found

                LCSTeam winner = LCSTeam.ParseTeam(win.InnerText);
                if (winner.Equals(team1) || winner.Equals(team2)) // Make sure detected winner was actually in this match
                {
                    LCSTeam other;
                    if (winner.Equals(team1))
                        other = team2;
                    else
                        other = team1;

                    record.Add(new FinishedMatch(winner, other, DateTime.Now.AddMinutes(-2)));
                }
            }
            return record;
        }
    }

    class Schedule : List<ScheduledMatch>
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
                if (!regex_schedule.Success)
                    continue;

                // Extract data from match schedule listing
                string t1 = regex_schedule.Groups[1].Value;
                string t2 = regex_schedule.Groups[2].Value;
                string year = regex_schedule.Groups[3].Value;
                string month = regex_schedule.Groups[4].Value;
                string day = regex_schedule.Groups[5].Value;
                string hour = regex_schedule.Groups[6].Value;
                string minute = regex_schedule.Groups[7].Value;
                DateTime match_utctime = DateTime.Parse($"{month}/{day}/{year} {hour}:{minute}:00", CultureInfo.InvariantCulture);
                // Apply timezone + daylight savings
                DateTime match_time = match_utctime + TimeZoneInfo.Local.GetUtcOffset(match_utctime + TimeZoneInfo.Local.BaseUtcOffset);
                LCSTeam team1 = LCSTeam.ParseTeam(t1);
                LCSTeam team2 = LCSTeam.ParseTeam(t2);
                if (team1.ID != LCSTeam.TeamID.Unknown && team2.ID != LCSTeam.TeamID.Unknown)
                {
                    ScheduledMatch scheduled_match = new ScheduledMatch(team1, team2, match_time);
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

        public ScheduledMatch GetNextMatchForTeam(LCSTeam team)
        {
            var nextMatchesForTeam = this.Where(match => DateTime.Now < match.Time + TimeSpan.FromMinutes(FUTURE_BUFFER_MINS) && (match.Team1.Equals(team) || match.Team2.Equals(team)));
            if (nextMatchesForTeam.Count() == 0)
                return null;
            return nextMatchesForTeam.First();
        }

        public ScheduledMatch GetNextMatchForMatchup(LCSTeam team1, LCSTeam team2)
        {
            var nextMatchups = this.Where(match =>
                DateTime.Now < match.Time + TimeSpan.FromMinutes(FUTURE_BUFFER_MINS) &&
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
        string SavePath = "scores.csv";

        public Scoreboard(IEnumerable<PlayerScore> scores, string path)
        {
            foreach (PlayerScore score in scores)
            {
                Add(score.DiscordID, score);
            }
            SavePath = path;
        }

        public Scoreboard()
        {
        }

        public void GiveMoney(ulong playerId, decimal money)
        {
            this[playerId].Money += money;
            SaveToFile();
        }

        public void TakeMoney(ulong playerId, decimal money)
        {
            this[playerId].Money -= money;
            SaveToFile();
        }

        public void MakePlayerBet(ulong playerId, decimal wager)
        {
            this[playerId].BetCount += 1;
            TakeMoney(playerId, wager); // Saves to file
        }

        public void AwardCorrectPrediction(ulong playerId)
        {
            this[playerId].PredictionsRight += 1;
            GiveMoney(playerId, Prediction.AWARD);
            SaveToFile();
        }

        public void AwardIncorrectPrediction(ulong playerId)
        {
            this[playerId].PredictionsWrong += 1;
            SaveToFile();
        }

        public void SaveToFile() // CSV
        {
            CSVHelper helper = new CSVHelper();
            List<IList<object>> data = new List<IList<object>>();
            foreach (PlayerScore score in Values)
            {
                List<object> columns = new List<object>
                {
                    score.DiscordID,
                    score.Name,
                    score.Money,
                    score.PredictionsRight,
                    score.PredictionsWrong,
                    score.BetCount
                };
                data.Add(columns);
            }
            helper.SaveToCsvFile(SavePath, data);
        }

        public static Scoreboard FromFile(string path) // CSV
        {
            List<PlayerScore> loaded_scores = new List<PlayerScore>();
            CSVHelper helper = new CSVHelper();
            try
            {
                List<List<string>> data = helper.ReadCsvFile(path);
                foreach (var row in data)
                {
                    if (row.Count == 6)
                    {
                        loaded_scores.Add(new PlayerScore(ulong.Parse(row[0]), row[1],
                            money: decimal.Parse(row[2]), pRight: int.Parse(row[3]), pWrong: int.Parse(row[4]), betCount: int.Parse(row[5])));
                    }
                }
                return new Scoreboard(loaded_scores, path);
            }
            catch
            {
                throw new IOException("Error loading Scoreboard from file! Scoreboard is corrupt!");
            }
        }
    }
}
