namespace StateLand.Models.BallotingDTOs.Response
{
    public class LotteryDataDTO
    {

        public int ApplicationId { get; set; }
        public string LotUniqueId { get; set; }
        public int DistrictId { get; set; }
        public int TehsilId { get; set; }
        public int MauzaId { get; set; }
        public string ApplicantName { get; set; }

        public string DistrictName { get; set; }

        public string DistrictNameEnglish { get; set; }

        public string TehsilName { get; set; }

        public string TehsilNameEnglish { get; set; }

        public string MouzaName { get; set; }

        public string MouzaNameEnglish { get; set; }

        public long ApplicantId { get; set; }

        public string CNIC { get; set; }
        public int LotId { get; set; }

    }
}
