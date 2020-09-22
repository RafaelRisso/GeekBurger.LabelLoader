namespace GeekBurger.LabelLoader
{
    public class LabelImageAdded
    {
        public LabelImageAdded()
        {
            ItemName = "meat";
            Ingredients = new string[3] { "diary", "gluten", "soy" };
        }

        public string ItemName { get; set; }
        public string[] Ingredients { get; set; }
    }
}
