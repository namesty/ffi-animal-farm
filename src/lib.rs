use std::{collections::HashMap, ffi::CStr};

extern "C" {
  fn speak(animal: *const std::ffi::c_void, message: *const i8);
}

struct Farm {
  pub animal_ptrs: HashMap<String, *const std::ffi::c_void>
}

impl Farm {
  pub fn add_animal(&mut self, name: &str, animal_ptr: *const std::ffi::c_void) {
    self.animal_ptrs.insert(name.to_string(), animal_ptr);
  }
}

#[no_mangle]
pub extern "C" fn add_animal(farm_ptr: *const std::ffi::c_void, animal_name: *const i8, animal_ptr: *const std::ffi::c_void) {
  let mut farm = unsafe {
    Box::from_raw(farm_ptr as *mut Farm)
  };

  let animal_name_cstr = unsafe { CStr::from_ptr(animal_name) };
  let animal_name = match animal_name_cstr.to_str() {
      Ok(u) => u.to_string(),
      Err(_) => panic!("Couldn't get CStr for URI")
  };

  farm.add_animal(&animal_name, animal_ptr)
}

#[no_mangle]
pub extern "C" fn create_farm() -> *const std::ffi::c_void {
  let farm = Farm {
    animal_ptrs: HashMap::new()
  };

  Box::into_raw(Box::new(farm)) as *const std::ffi::c_void
}

#[no_mangle]
pub extern "C" fn native_speak(farm_ptr: *const std::ffi::c_void, animal_name: *const i8, message: *const i8) {
  let farm = unsafe {
    Box::from_raw(farm_ptr as *mut Farm)
  };

  let animal_name_cstr = unsafe { CStr::from_ptr(animal_name) };
  let animal_name = match animal_name_cstr.to_str() {
      Ok(u) => u.to_string(),
      Err(_) => panic!("Couldn't get CStr for URI")
  };

  if let Some(animal_ptr) = farm.animal_ptrs.get(&animal_name) {
    unsafe {
      println!("Starting to speak from rust...");
      speak(*animal_ptr, message);
      println!("Finished speaking from rust")
    }
  } else {
    panic!("Animal with name {} could not be found in farm", animal_name)
  }

}
