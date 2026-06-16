namespace StateLand.Models.Entities.Ballotings
{
    public class BallotingResultEntity
    {

        public long BallotingRunId { get; set; }
        public string LotId { get; set; }
        public int ApplicationId { get; set; }
        public string Status { get; set; }

    }
}
