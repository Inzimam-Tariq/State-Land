namespace StateLand.Models.GIS
{
    public class DistrictModel
    {
        public int ID { get; set; }
        public string NAME { get; set; }
        public double MinX { get; set; } // if you have coordinates
        public double MinY { get; set; }
        public double MaxX { get; set; }
        public double MaxY { get; set; }
    }
}
