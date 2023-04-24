using uniffi.main;

public class AnimalImpl : Animal
{
  private readonly string name;
  private readonly string greeting;

  public AnimalImpl(string name, string greeting)
  {
    this.name = name;
    this.greeting = greeting;
  }
  public string GetName()
  {
    return this.name;
  }

  public string Speak(string msg)
  {
    var words = $"{this.greeting}! {msg}";

    Console.WriteLine(words);
    return words;
  }
}

internal class Program
{

  private static void Main(string[] args)
  {
    var farm = MainMethods.CreateFarm();
    
    var cat = new AnimalImpl("Cat", "Meow");
    var horse = new AnimalImpl("Horse", "Neigh");

    MainMethods.AddAnimal(farm, cat);
    MainMethods.AddAnimal(farm, horse);

    MainMethods.NativeSpeak(farm, "Cat", "Hello C# FFI!");
    MainMethods.NativeSpeak(farm, "Horse", "I like to eat oats and I like to kick goats!");
  }
}