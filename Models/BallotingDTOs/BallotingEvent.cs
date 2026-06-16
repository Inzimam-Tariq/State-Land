namespace StateLand.Models.BallotingDTOs
{
    public class BallotingEvent
    {
        public string Type { get; set; }   // LotStarted, Shuffle, WinnerPick, ReservedPick, LotCompleted
        public long RunId { get; set; }
        public string LotId { get; set; }
        public object Data { get; set; }
    }
}
