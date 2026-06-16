namespace StateLand.Models.BallotingDTOs.Response
{
    public class BallotingResponseDTO
    {
        public long RunId { get; set; }
        public List<BallotingResultDTO> Results { get; set; }

    }
}
