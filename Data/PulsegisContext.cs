using Microsoft.EntityFrameworkCore;
using StateLand.Hubs;
using StateLand.Models.Auth;
using StateLand.Models.BallotingDTOs;
using StateLand.Models.Entities;

namespace StateLand.Data
{
    public class PulsegisContext : DbContext
    {
        public PulsegisContext(DbContextOptions<PulsegisContext> options)
            : base(options)
        {
        }

        public DbSet<User> Users { get; set; }
        public DbSet<Assignment> Assignments { get; set; }
        public DbSet<ApplicantDetail> ApplicantDetail { get; set; }
    }
}
