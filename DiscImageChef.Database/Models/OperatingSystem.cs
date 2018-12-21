using System.ComponentModel.DataAnnotations;

namespace DiscImageChef.Database.Models
{
    public class OperatingSystem
    {
        [Key]
        public int Id { get;              set; }
        public string Name         { get; set; }
        public string Version      { get; set; }
        public bool   Synchronized { get; set; }
    }
}