namespace StateLand.Models.BallotingDTOs.Response
{
    public class BallotingResultDTO
    {


        public long BallotingRunId { get; set; }
        public string LotId { get; set; }
        public int ApplicationId { get; set; }
        public string Status { get; set; } // Winner | Reserved
        public int Lot_Number { get; set; }
        public int Mouza_Id { get; set; }
        public string ApplicantName { get; set; }
        public int Total_No_Of_Applications { get; set; }
        public string DistrictName { get; set; }

        public string DistrictNameEnglish { get; set; }

        public string TehsilName { get; set; }

        public string TehsilNameEnglish { get; set; }

        public string MouzaName { get; set; }

        public string MouzaNameEnglish { get; set; }
        public long ApplicantId { get; set; }
        public int District_Id { get; set; }
        public int Tehsil_Id { get; set; }

        public int Mauza_Id { get; set; }
        public string CNIC { get; set; }
    }
}
