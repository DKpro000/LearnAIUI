# For cuda version
pip uninstall -y torch torchvision torchaudio
pip cache purge
pip install torch torchvision torchaudio --index-url https://download.pytorch.org/whl/cu128

# Unity json
com.unity.nuget.newtonsoft-json