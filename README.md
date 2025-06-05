
# UnityTerrainGrass

### Usage
1. Create Shader, Material and prefab(s)

![Step 1](https://darkblue.tech/terraingrass/step1.png)

2. Add Terrain Grass script to Terrain object
   
![Step 2](https://darkblue.tech/terraingrass/step2.png)

3. Make shure that **GPU Instancing** is **enabled**
   
![Step 3](https://darkblue.tech/terraingrass/step3.png)

4. Assign prefab(s) to LOD levels
   
![Step 4](https://darkblue.tech/terraingrass/step4.png)

5. Enjoy!
    
![Step 5](https://darkblue.tech/terraingrass/step5.png)

### Common troubles
- Make shure that your Main Camera has MainCamera tag
- SeaLevel is a global Y coordinate, if grass does not spawns adjust SeaLevel to be lower than your terrain Y
