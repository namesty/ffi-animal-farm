use std::sync::{Mutex, Arc};
use std::{collections::HashMap};
use std::fmt::Debug;

pub trait Animal: Send + Sync + Debug {
  fn get_name(&self) -> String;
  fn speak(&self, msg: String) -> String;
}

#[derive(Debug)]
pub struct Farm {
  animals: Mutex<HashMap<String, Box<dyn Animal>>>
}

impl Farm {
  pub fn new() -> Self {
    Farm {
      animals: Mutex::new(HashMap::new())
    }
  }

  pub fn add_animal(&self, animal: Box<dyn Animal>) {
      let animal_name = animal.get_name();
      self.animals.lock().unwrap().insert(animal_name, animal);
  }

  pub fn remove_animal(&self, _: &str) {
    unimplemented!()
  }
}

pub fn add_animal(
    farm: Arc<Farm>,
    animal: Box<dyn Animal>,
) {
    farm.add_animal(animal);
}

pub fn remove_animal(
    farm: Arc<Farm>,
    animal_name: &str
) {
    farm.remove_animal(&animal_name);
}

pub fn get_animal(
    farm: Arc<Farm>,
    animal_name: &str,
) -> Box<dyn Animal> {
    if let Some(animal) = farm.animals.lock().unwrap().remove(animal_name) {
        animal
    } else {
        panic!(
            "Animal with name {} could not be found in farm",
            animal_name
        )
    }
}

pub fn create_farm() -> Arc<Farm> {
    Arc::new(Farm::new())
}

pub fn native_speak(
    farm: Arc<Farm>,
    animal_name: &str,
    message: &str,
) {
    if let Some(animal) = farm.animals.lock().unwrap().get(animal_name) {
        animal.speak(message.to_string());
    } else {
        panic!(
            "Animal with name {} could not be found in farm",
            animal_name
        )
    }
}

uniffi::include_scaffolding!("main");